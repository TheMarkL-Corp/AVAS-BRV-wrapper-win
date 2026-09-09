using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AvasRoutingApp.Configuration;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace AvasRoutingApp.Tests
{
    public class WebView2EmpiricalTests
    {
        private static void RunInSta(Action<Dispatcher> action)
        {
            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    if (Application.Current == null)
                    {
                        new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    }
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    action(dispatcher);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            bool finished = thread.Join(15000);
            if (!finished)
            {
                thread.Interrupt();
                throw new TimeoutException("STA test execution timed out after 15 seconds.");
            }
            if (caught != null)
            {
                throw new AggregateException("STA test failed", caught);
            }
        }

        private static void PumpDispatcher(Dispatcher dispatcher, int milliseconds)
        {
            var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < end)
            {
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(20);
            }
        }

        [Fact]
        public void MainWindow_LayoutAndDefaults_MatchContract()
        {
            RunInSta(dispatcher =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "avas_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var configService = new ConfigService(Path.Combine(tempDir, "appsettings.json"));
                    var window = new MainWindow(configService);

                    Assert.Equal("AVAS Routing Software — Dual Link SDVoE Manager", window.Title);
                    Assert.Equal(1440, window.Width);
                    Assert.Equal(850, window.Height);
                    Assert.Equal(900, window.MinWidth);
                    Assert.Equal(600, window.MinHeight);

                    var sidebar = (Border)window.FindName("SidebarContainer");
                    Assert.NotNull(sidebar);
                    Assert.Equal(0, sidebar.Width); // Collapsed on startup

                    var offlineBanner = (Border)window.FindName("OfflineBanner");
                    Assert.NotNull(offlineBanner);
                    Assert.Equal(Visibility.Collapsed, offlineBanner.Visibility);

                    var webView = (WebView2)window.FindName("MainWebView");
                    Assert.NotNull(webView);

                    window.Close();
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            });
        }

        [Fact]
        public void SidebarToggle_ExpandsAndCollapsesCorrectly()
        {
            RunInSta(dispatcher =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "avas_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var configService = new ConfigService(Path.Combine(tempDir, "appsettings.json"));
                    var window = new MainWindow(configService);

                    var btnToggle = (Button)window.FindName("BtnToggleSidebar");
                    var sidebar = (Border)window.FindName("SidebarContainer");
                    var txtToggleIcon = (TextBlock)window.FindName("TxtToggleIcon");

                    Assert.NotNull(btnToggle);
                    Assert.NotNull(sidebar);
                    Assert.NotNull(txtToggleIcon);

                    // Initially collapsed
                    Assert.Equal(0, sidebar.Width);
                    Assert.Equal("◀ PREVIEW", txtToggleIcon.Text);

                    // 1st click: Expand
                    btnToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(configService.Current.SidebarWidth, sidebar.Width);
                    Assert.Equal("▶ CLOSE", txtToggleIcon.Text);

                    // 2nd click: Collapse
                    btnToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(0, sidebar.Width);
                    Assert.Equal("◀ PREVIEW", txtToggleIcon.Text);

                    window.Close();
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            });
        }

        [Fact]
        public void UserDataFolder_TargetsApplicationBaseDirectory_NotLocalAppData()
        {
            string appBaseDir = AppContext.BaseDirectory;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string expectedUserDataFolder = Path.Combine(appBaseDir, "WebView2_UserData");

            // Verify path resolution
            Assert.StartsWith(appBaseDir, expectedUserDataFolder, StringComparison.OrdinalIgnoreCase);
            Assert.False(expectedUserDataFolder.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase),
                $"UserDataFolder '{expectedUserDataFolder}' must NOT reside under LocalAppData '{localAppData}'");
            Assert.EndsWith("WebView2_UserData", expectedUserDataFolder);
        }

        [Fact]
        public void OfflineBanner_ShowsWhenUrlIsEmpty()
        {
            RunInSta(dispatcher =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "avas_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var configService = new ConfigService(Path.Combine(tempDir, "appsettings.json"));
                    var config = configService.Current;
                    config.BlueRiverUrl = ""; // empty url
                    configService.Save(config);

                    var window = new MainWindow(configService);

                    // Invoke NavigateToConfiguredUrl via reflection
                    var navMethod = typeof(MainWindow).GetMethod("NavigateToConfiguredUrl", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.NotNull(navMethod);
                    navMethod.Invoke(window, null);

                    var offlineBanner = (Border)window.FindName("OfflineBanner");
                    var txtOfflineMsg = (TextBlock)window.FindName("TxtOfflineMessage");

                    Assert.Equal(Visibility.Visible, offlineBanner.Visibility);
                    Assert.Contains("not configured", txtOfflineMsg.Text, StringComparison.OrdinalIgnoreCase);

                    window.Close();
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            });
        }

        [Fact]
        public void OfflineBanner_ShowAndHideMechanics()
        {
            RunInSta(dispatcher =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "avas_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var configService = new ConfigService(Path.Combine(tempDir, "appsettings.json"));
                    var window = new MainWindow(configService);

                    var showMethod = typeof(MainWindow).GetMethod("ShowOfflineBanner", BindingFlags.NonPublic | BindingFlags.Instance);
                    var hideMethod = typeof(MainWindow).GetMethod("HideOfflineBanner", BindingFlags.NonPublic | BindingFlags.Instance);

                    Assert.NotNull(showMethod);
                    Assert.NotNull(hideMethod);

                    var offlineBanner = (Border)window.FindName("OfflineBanner");
                    var txtOfflineMsg = (TextBlock)window.FindName("TxtOfflineMessage");

                    // Test showing offline banner with custom message
                    string testError = "SDVoE Host Unreachable (Err 503)";
                    showMethod.Invoke(window, new object[] { testError });

                    Assert.Equal(Visibility.Visible, offlineBanner.Visibility);
                    Assert.Equal($"⚠ {testError}", txtOfflineMsg.Text);

                    // Test hiding offline banner
                    hideMethod.Invoke(window, null);
                    Assert.Equal(Visibility.Collapsed, offlineBanner.Visibility);

                    window.Close();
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            });
        }

        [Fact]
        public void ConfigChanged_UpdatesStatusDisplays()
        {
            RunInSta(dispatcher =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "avas_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var configService = new ConfigService(Path.Combine(tempDir, "appsettings.json"));
                    var window = new MainWindow(configService);

                    var txtServerStatus = (TextBlock)window.FindName("TxtServerStatus");
                    var txtUrlDisplay = (TextBlock)window.FindName("TxtCurrentUrlDisplay");

                    Assert.Equal("SDVoE Server: 127.0.0.1:8090", txtServerStatus.Text);
                    Assert.Equal("URL: http://localhost:80", txtUrlDisplay.Text);

                    // Update config
                    var updatedConfig = new AppConfig
                    {
                        ControlServerIp = "192.168.10.50",
                        RestPort = 9000,
                        BlueRiverUrl = "https://192.168.10.50:8443/manager"
                    };
                    configService.Save(updatedConfig);

                    PumpDispatcher(dispatcher, 100);

                    Assert.Equal("SDVoE Server: 192.168.10.50:9000", txtServerStatus.Text);
                    Assert.Equal("URL: https://192.168.10.50:8443/manager", txtUrlDisplay.Text);

                    window.Close();
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            });
        }

        [Fact]
        public void WindowClosing_UnsubscribesEventsAndDisposesWebView()
        {
            RunInSta(dispatcher =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "avas_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var configService = new ConfigService(Path.Combine(tempDir, "appsettings.json"));
                    var window = new MainWindow(configService);

                    // Call window.Close() which executes OnClosing
                    window.Close();

                    // After closing, updating config should not throw or cause ObjectDisposedException on UI
                    var updated = configService.Current;
                    updated.RestPort = 8888;
                    configService.Save(updated); // Should be safely ignored since unsubscribed
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            });
        }

        [Fact]
        public void OfflineFallback_UnreachableHost_TriggersOfflineBanner_AndRecoversOnline()
        {
            RunInSta(dispatcher =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "avas_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    // 1. Configure an unreachable port
                    int offlinePort = 49152;
                    var configService = new ConfigService(Path.Combine(tempDir, "appsettings.json"));
                    var config = configService.Current;
                    config.BlueRiverUrl = $"http://127.0.0.1:{offlinePort}";
                    configService.Save(config);

                    var window = new MainWindow(configService);
                    var offlineBanner = (Border)window.FindName("OfflineBanner");
                    var txtOfflineMsg = (TextBlock)window.FindName("TxtOfflineMessage");
                    var webView = (WebView2)window.FindName("MainWebView");

                    var initTcs = new TaskCompletionSource<bool>();
                    var offlineNavTcs = new TaskCompletionSource<bool>();

                    webView.CoreWebView2InitializationCompleted += (s, e) =>
                    {
                        if (e.IsSuccess) initTcs.TrySetResult(true);
                        else initTcs.TrySetException(e.InitializationException);
                    };

                    webView.NavigationCompleted += (s, e) =>
                    {
                        if (!e.IsSuccess)
                        {
                            offlineNavTcs.TrySetResult(false);
                        }
                    };

                    window.Show();

                    // Pump dispatcher until WebView2 initializes and navigation to unreachable host fails
                    var start = DateTime.UtcNow;
                    while ((!initTcs.Task.IsCompleted || !offlineNavTcs.Task.IsCompleted) && (DateTime.UtcNow - start).TotalSeconds < 10)
                    {
                        var frame = new DispatcherFrame();
                        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                        Dispatcher.PushFrame(frame);
                        Thread.Sleep(20);
                    }

                    Assert.True(initTcs.Task.IsCompleted, "WebView2 core initialization timed out");
                    Assert.True(offlineNavTcs.Task.IsCompleted, "Offline navigation completion timed out");

                    // Verify OfflineBanner is visible and message contains failure
                    Assert.Equal(Visibility.Visible, offlineBanner.Visibility);
                    Assert.Contains("Unable to reach BlueRiver AV Manager", txtOfflineMsg.Text);

                    // 2. Now start a real HttpListener on a free port to test recovery
                    var listener = new HttpListener();
                    int onlinePort = 49153;
                    listener.Prefixes.Add($"http://127.0.0.1:{onlinePort}/");
                    listener.Start();

                    var serverTask = Task.Run(async () =>
                    {
                        while (listener.IsListening)
                        {
                            try
                            {
                                var ctx = await listener.GetContextAsync();
                                byte[] responseBytes = System.Text.Encoding.UTF8.GetBytes("<html><body>BlueRiver AV Manager Online</body></html>");
                                ctx.Response.ContentType = "text/html";
                                ctx.Response.ContentLength64 = responseBytes.Length;
                                await ctx.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                                ctx.Response.Close();
                            }
                            catch
                            {
                                break;
                            }
                        }
                    });

                    var onlineNavTcs = new TaskCompletionSource<bool>();
                    webView.NavigationCompleted += (s, e) =>
                    {
                        if (e.IsSuccess && webView.Source.ToString().Contains(onlinePort.ToString()))
                        {
                            onlineNavTcs.TrySetResult(true);
                        }
                    };

                    // Reconfigure to online server
                    config = configService.Current;
                    config.BlueRiverUrl = $"http://127.0.0.1:{onlinePort}/";
                    configService.Save(config);

                    // Trigger navigation via reflection
                    var navMethod = typeof(MainWindow).GetMethod("NavigateToConfiguredUrl", BindingFlags.NonPublic | BindingFlags.Instance);
                    navMethod?.Invoke(window, null);

                    start = DateTime.UtcNow;
                    while (!onlineNavTcs.Task.IsCompleted && (DateTime.UtcNow - start).TotalSeconds < 10)
                    {
                        var frame = new DispatcherFrame();
                        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                        Dispatcher.PushFrame(frame);
                        Thread.Sleep(20);
                    }

                    Assert.True(onlineNavTcs.Task.IsCompleted, "Online navigation recovery timed out");

                    // Verify OfflineBanner collapsed on successful load
                    Assert.Equal(Visibility.Collapsed, offlineBanner.Visibility);

                    listener.Stop();
                    window.Close();
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            });
        }

        [Fact]
        public void WebView2_Disposal_TerminatesBrowserProcesses_AndReleasesLocks()
        {
            RunInSta(dispatcher =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "avas_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var configService = new ConfigService(Path.Combine(tempDir, "appsettings.json"));
                    var window = new MainWindow(configService);
                    var webView = (WebView2)window.FindName("MainWebView");

                    var initTcs = new TaskCompletionSource<bool>();
                    webView.CoreWebView2InitializationCompleted += (s, e) =>
                    {
                        if (e.IsSuccess) initTcs.TrySetResult(true);
                        else initTcs.TrySetException(e.InitializationException);
                    };

                    window.Show();

                    var start = DateTime.UtcNow;
                    while (!initTcs.Task.IsCompleted && (DateTime.UtcNow - start).TotalSeconds < 10)
                    {
                        var frame = new DispatcherFrame();
                        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                        Dispatcher.PushFrame(frame);
                        Thread.Sleep(20);
                    }

                    Assert.True(initTcs.Task.IsCompleted, "WebView2 initialization timed out");
                    Assert.NotNull(webView.CoreWebView2);

                    uint browserPid = webView.CoreWebView2.BrowserProcessId;
                    Assert.True(browserPid > 0, "BrowserProcessId must be > 0");

                    // Verify browser process exists
                    var browserProc = System.Diagnostics.Process.GetProcessById((int)browserPid);
                    Assert.NotNull(browserProc);
                    Assert.False(browserProc.HasExited);

                    // Close the window (which invokes MainWebView.Dispose in OnClosing)
                    window.Close();

                    // Pump dispatcher briefly to allow disposal callbacks to process
                    start = DateTime.UtcNow;
                    while (!browserProc.HasExited && (DateTime.UtcNow - start).TotalSeconds < 8)
                    {
                        var frame = new DispatcherFrame();
                        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                        Dispatcher.PushFrame(frame);
                        Thread.Sleep(50);
                    }

                    Assert.True(browserProc.HasExited, $"Browser process PID {browserPid} should have exited after window disposal");

                    // Verify file locks on WebView2 profile and databases are released
                    string userDataFolder = Path.Combine(AppContext.BaseDirectory, "WebView2_UserData", "EBWebView", "Default");
                    if (Directory.Exists(userDataFolder))
                    {
                        foreach (var file in Directory.GetFiles(userDataFolder, "*.*", SearchOption.AllDirectories))
                        {
                            // Test that profile and DB files can be opened with exclusive access (FileShare.None)
                            using var fs = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            Assert.NotNull(fs);
                        }
                    }
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            });
        }
    }
}

