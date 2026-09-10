using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AvasRoutingSetup.Engine;
using Microsoft.Win32;

namespace AvasRoutingSetup;

public partial class MainWindow : Window
{
    private string _targetInstallDir = string.Empty;
    private bool _isInstalling = false;
    private bool _isCompleted = false;

    private bool _isDotNet8Installed = false;
    private bool _isWebView2Installed = false;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. Initialize default install path (%LocalAppData%\Programs\Advantech\AVAS Routing SW)
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _targetInstallDir = Path.Combine(localAppData, @"Programs\Advantech\AVAS Routing SW");
        TxtInstallPath.Text = _targetInstallDir;

        // 2. Pre-flight dependency check
        await RunPreflightChecksAsync();
    }

    private async Task RunPreflightChecksAsync()
    {
        await Task.Run(() =>
        {
            var dotNetCheck = DependencyDetector.CheckDotNet8DesktopRuntime();
            _isDotNet8Installed = dotNetCheck.IsInstalled;

            var webView2Check = DependencyDetector.CheckWebView2Runtime();
            _isWebView2Installed = webView2Check.IsInstalled;

            Dispatcher.Invoke(() =>
            {
                if (_isDotNet8Installed)
                {
                    IconDotNet.Text = "✔";
                    IconDotNet.Foreground = (Brush)FindResource("SuccessBrush");
                    TxtDotNetStatus.Text = dotNetCheck.VersionInfo;
                    TxtDotNetStatus.Foreground = (Brush)FindResource("SuccessBrush");
                }
                else
                {
                    IconDotNet.Text = "📦";
                    IconDotNet.Foreground = (Brush)FindResource("WarningBrush");
                    TxtDotNetStatus.Text = "Will install from local package";
                    TxtDotNetStatus.Foreground = (Brush)FindResource("WarningBrush");
                }

                if (_isWebView2Installed)
                {
                    IconWebView2.Text = "✔";
                    IconWebView2.Foreground = (Brush)FindResource("SuccessBrush");
                    TxtWebView2Status.Text = webView2Check.VersionInfo;
                    TxtWebView2Status.Foreground = (Brush)FindResource("SuccessBrush");
                }
                else
                {
                    IconWebView2.Text = "📦";
                    IconWebView2.Foreground = (Brush)FindResource("WarningBrush");
                    TxtWebView2Status.Text = "Will install from local package";
                    TxtWebView2Status.Foreground = (Brush)FindResource("WarningBrush");
                }
            });
        });
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Destination Directory for AVAS Routing Software",
            InitialDirectory = Directory.Exists(TxtInstallPath.Text) ? TxtInstallPath.Text : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dialog.ShowDialog() == true)
        {
            TxtInstallPath.Text = dialog.FolderName;
            _targetInstallDir = dialog.FolderName;
        }
    }

    private async void BtnAction_Click(object sender, RoutedEventArgs e)
    {
        if (_isCompleted)
        {
            // Launch application if requested
            if (ChkLaunchApp.IsChecked == true)
            {
                string exePath = Path.Combine(_targetInstallDir, "AvasRoutingApp.exe");
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = _targetInstallDir, UseShellExecute = true });
                }
            }
            Close();
            return;
        }

        if (_isInstalling) return;

        // Start installation
        _targetInstallDir = TxtInstallPath.Text.Trim();
        if (string.IsNullOrEmpty(_targetInstallDir))
        {
            MessageBox.Show("Please select a valid installation path.", "Invalid Path", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isInstalling = true;
        BtnAction.IsEnabled = false;
        BtnCancel.IsEnabled = false;

        ViewConfig.Visibility = Visibility.Collapsed;
        ViewProgress.Visibility = Visibility.Visible;
        TxtFooterStatus.Text = "Installing...";

        await ExecuteInstallationPipelineAsync();
    }

    private async Task ExecuteInstallationPipelineAsync()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string redistDir = Path.Combine(baseDir, "redist");
        string payloadDir = Path.Combine(baseDir, "payload");
        string whiteLabelDir = Path.Combine(baseDir, "whitelabel");

        Log("=== AVAS Routing Software Installation Started ===");
        Log($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"Target Directory: {_targetInstallDir}");

        // ==========================================
        // Step 1: .NET 8 Windows Desktop Runtime
        // ==========================================
        ProgressBarMain.Value = 10;
        TxtProgressTitle.Text = "Verifying .NET 8 Windows Desktop Runtime...";
        if (_isDotNet8Installed)
        {
            Log("• .NET 8 Desktop Runtime is already installed. Skipping installer.");
        }
        else
        {
            string dotnetInstaller = Path.Combine(redistDir, "windowsdesktop-runtime-8.0.30-win-x64.exe");
            if (!File.Exists(dotnetInstaller))
            {
                // Fallback check in parent or Downloads
                dotnetInstaller = FindLocalRedistFile("windowsdesktop-runtime-*.exe", redistDir);
            }

            if (File.Exists(dotnetInstaller))
            {
                Log($"• Installing .NET 8 Desktop Runtime from local package: {Path.GetFileName(dotnetInstaller)}...");
                var res = await DependencyInstaller.InstallDotNet8RuntimeAsync(dotnetInstaller, Log);
                if (!res.Success)
                {
                    Log($"• Warning: .NET 8 installer reported: {res.Message}");
                }
            }
            else
            {
                Log("• Notice: Local .NET 8 redistributable not found in redist folder. Skipping.");
            }
        }

        // ==========================================
        // Step 2: Microsoft Edge WebView2 Runtime
        // ==========================================
        ProgressBarMain.Value = 25;
        TxtProgressTitle.Text = "Verifying Microsoft Edge WebView2 Runtime...";
        if (_isWebView2Installed)
        {
            Log("• Microsoft Edge WebView2 Runtime is already installed. Skipping installer.");
        }
        else
        {
            string wv2Installer = Path.Combine(redistDir, "MicrosoftEdgeWebView2RuntimeInstallerX64.exe");
            if (!File.Exists(wv2Installer))
            {
                wv2Installer = FindLocalRedistFile("*WebView2*.exe", redistDir);
            }

            if (File.Exists(wv2Installer))
            {
                Log($"• Installing Microsoft Edge WebView2 Runtime from local package: {Path.GetFileName(wv2Installer)}...");
                var res = await DependencyInstaller.InstallWebView2RuntimeAsync(wv2Installer, Log);
                if (!res.Success)
                {
                    Log($"• Warning: WebView2 installer reported: {res.Message}");
                }
            }
            else
            {
                Log("• Notice: Local WebView2 redistributable not found in redist folder. Skipping.");
            }
        }

        // ==========================================
        // Step 3: Extract and Deploy Payload
        // ==========================================
        ProgressBarMain.Value = 45;
        TxtProgressTitle.Text = "Deploying AVAS Routing Software files...";

        // Look for payload directory or zip file
        string payloadSource = payloadDir;
        if (!Directory.Exists(payloadSource))
        {
            string zipFile = Path.Combine(baseDir, "payload.zip");
            if (!File.Exists(zipFile))
            {
                zipFile = Path.Combine(baseDir, "AVAS-Routing-SW-v1.1.0-win-x64.zip");
            }
            if (File.Exists(zipFile))
            {
                payloadSource = zipFile;
            }
        }

        var deployProgress = new Progress<(int Current, int Total, string Status)>(p =>
        {
            double pct = 45 + ((double)p.Current / Math.Max(1, p.Total)) * 35;
            ProgressBarMain.Value = pct;
            TxtProgressSubtitle.Text = p.Status;
        });

        var deployRes = await AppDeployer.DeployAsync(payloadSource, _targetInstallDir, deployProgress, Log);
        if (!deployRes.Success)
        {
            Log($"• Error during deployment: {deployRes.Message}");
        }
        else
        {
            Log("• Application payload successfully installed.");
        }

        // ==========================================
        // Step 4: Shortcuts
        // ==========================================
        ProgressBarMain.Value = 85;
        TxtProgressTitle.Text = "Configuring shortcuts...";
        string targetExe = Path.Combine(_targetInstallDir, "AvasRoutingApp.exe");
        string targetIcon = Path.Combine(_targetInstallDir, "logo.ico");

        if (ChkDesktopShortcut.IsChecked == true)
        {
            ShortcutHelper.CreateDesktopShortcut(targetExe, targetIcon, Log);
        }

        if (ChkStartMenuShortcut.IsChecked == true)
        {
            ShortcutHelper.CreateStartMenuShortcut(targetExe, targetIcon, Log);
        }

        // ==========================================
        // Step 5: Advantech White-Labeling (Optional)
        // ==========================================
        if (ChkWhiteLabel.IsChecked == true)
        {
            ProgressBarMain.Value = 92;
            TxtProgressTitle.Text = "Applying Advantech White-Labeling to BlueRiver AV Manager...";
            Log("• Applying Advantech White-Labeling...");
            var wlRes = await WhiteLabelManager.ApplyWhiteLabelAsync(whiteLabelDir, Log);
            Log($"• White-labeling result: {wlRes.Message}");
        }

        // ==========================================
        // Step 6: Register in Windows Add/Remove Programs
        // ==========================================
        ProgressBarMain.Value = 98;
        TxtProgressTitle.Text = "Registering Windows application...";
        UninstallRegistryHelper.Register(_targetInstallDir, "1.2.0", Log);

        ProgressBarMain.Value = 100;
        Log("=== Installation Completed Successfully ===");

        await Task.Delay(800);

        // Transition to Finished View
        _isCompleted = true;
        _isInstalling = false;

        ViewProgress.Visibility = Visibility.Collapsed;
        ViewFinished.Visibility = Visibility.Visible;

        TxtSummaryDest.Text = $"• Location: {_targetInstallDir}";
        TxtSummaryShortcuts.Text = $"• Shortcuts: Desktop: {(ChkDesktopShortcut.IsChecked == true ? "Created" : "None")}, Start Menu: {(ChkStartMenuShortcut.IsChecked == true ? "Created" : "None")}";
        TxtSummaryWhiteLabel.Text = $"• Advantech White-Labeling: {(ChkWhiteLabel.IsChecked == true ? "Applied" : "Not requested")}";

        TxtFooterStatus.Text = "Setup complete";
        BtnAction.Content = "Finish";
        BtnAction.IsEnabled = true;
        BtnCancel.Visibility = Visibility.Collapsed;
    }

    private string FindLocalRedistFile(string pattern, string preferredDir)
    {
        try
        {
            if (Directory.Exists(preferredDir))
            {
                var files = Directory.GetFiles(preferredDir, pattern);
                if (files.Length > 0) return files[0];
            }

            // Fallback to current directory
            var localFiles = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, pattern);
            if (localFiles.Length > 0) return localFiles[0];

            // Fallback to User Downloads
            string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(userDownloads))
            {
                var dlFiles = Directory.GetFiles(userDownloads, pattern);
                if (dlFiles.Length > 0) return dlFiles[0];
            }
        }
        catch { }

        return string.Empty;
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            TxtLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            ScrollLogs.ScrollToEnd();
        });
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
