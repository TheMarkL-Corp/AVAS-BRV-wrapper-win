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
        public void VersionString_ReturnsConsistentV110()
        {
            Assert.Equal("v1.1.0", AppInfo.VersionString);
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
            Assert.Equal(1, ver.Minor);
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
            });
        }

        private static void RunInSta(Action action)
        {
            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            bool finished = thread.Join(10000);
            Assert.True(finished, "STA thread timed out.");
            if (caught != null)
            {
                throw new AggregateException("Exception thrown on STA thread", caught);
            }
        }
    }
}
