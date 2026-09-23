using System;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Views;
using Xunit;

namespace AvasRoutingApp.Tests
{
    public class AppInfoTests
    {
        [Fact]
        public void VersionString_ReturnsConsistentVersion()
        {
            Assert.Equal("v1.4.0", AppInfo.VersionString);
            Assert.Equal("AVAS Routing Software", AppInfo.AppName);
            Assert.Equal("Dual Link SDVoE Manager", AppInfo.AppSubtitle);
        }

        [Fact]
        public void AssemblyMetadata_MatchesAppInfoVersion()
        {
            var asm = typeof(AppInfo).Assembly;
            var ver = asm.GetName().Version;
            Assert.NotNull(ver);
            Assert.Equal(1, ver.Major);
            Assert.Equal(4, ver.Minor);
            Assert.Equal(0, ver.Build);
        }

        [Fact]
        public void MainWindow_VersionText_MatchesAppInfo()
        {
            RunInSta(() =>
            {
                var window = new MainWindow(new ConfigService());
                var txtVersion = (TextBlock)window.FindName("TxtAppVersion");
                Assert.NotNull(txtVersion);
                Assert.Equal(AppInfo.VersionString, txtVersion.Text);
            });
        }

        [Fact]
        public void SettingsDialog_VersionText_MatchesAppInfo()
        {
            RunInSta(() =>
            {
                var dialog = new SettingsDialog(new ConfigService());
                var txtSettingsVersion = (TextBlock)dialog.FindName("TxtSettingsAppVersion");
                Assert.NotNull(txtSettingsVersion);
                Assert.Equal(AppInfo.VersionString, txtSettingsVersion.Text);
                dialog.Measure(new System.Windows.Size(520, 660));
                dialog.Arrange(new System.Windows.Rect(0, 0, 520, 660));
                dialog.UpdateLayout();
            });
        }

        [Fact]
        public void SettingsDialog_Show_LightAndDarkThemes_DoesNotThrow()
        {
            RunInSta(() =>
            {
                var app = System.Windows.Application.Current ?? new System.Windows.Application();
                
                // Test Light Theme
                AvasRoutingApp.App.ApplyTheme("Light");
                var dialogLight = new SettingsDialog(new ConfigService());
                dialogLight.Show();
                dialogLight.UpdateLayout();
                dialogLight.Close();

                // Test Dark Theme
                AvasRoutingApp.App.ApplyTheme("Dark");
                var dialogDark = new SettingsDialog(new ConfigService());
                dialogDark.Show();
                dialogDark.UpdateLayout();
                dialogDark.Close();
            });
        }

        private static void RunInSta(Action action)
        {
            WpfTestHelper.Run(action);
        }
    }
}
