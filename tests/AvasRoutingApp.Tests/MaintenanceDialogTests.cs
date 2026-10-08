using System;
using System.Windows;
using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.ViewModels;
using AvasRoutingApp.Views;
using Xunit;

namespace AvasRoutingApp.Tests
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class WpfFactAttribute : FactAttribute
    {
    }

    public class MaintenanceDialogTests
    {
        [WpfFact]
        public void MaintenanceDialog_Instantiate_LightTheme_DoesNotThrow()
        {
            WpfTestHelper.Run(() =>
            {
                var app = Application.Current ?? new Application();
                App.ApplyTheme("Light");

                var dev = new AvasDevice
                {
                    MacAddress = "00:14:95:11:22:33",
                    CompanionMac = "00:14:95:11:22:34",
                    LinkMode = "DUAL"
                };
                var mockController = new MockMulticastController();
                var vm = new MaintenanceViewModel(dev, mockController);

                var dialog = new MaintenanceDialog(vm);
                Assert.NotNull(dialog);
                dialog.Measure(new Size(520, 560));
                dialog.Arrange(new Rect(0, 0, 520, 560));
                dialog.UpdateLayout();
            });
        }

        [WpfFact]
        public void MaintenanceDialog_Instantiate_DarkTheme_DoesNotThrow()
        {
            WpfTestHelper.Run(() =>
            {
                var app = Application.Current ?? new Application();
                App.ApplyTheme("Dark");

                var dev = new AvasDevice
                {
                    MacAddress = "00:14:95:11:22:33",
                    CompanionMac = "00:14:95:11:22:34",
                    LinkMode = "DUAL"
                };
                var mockController = new MockMulticastController();
                var vm = new MaintenanceViewModel(dev, mockController);

                var dialog = new MaintenanceDialog(vm);
                Assert.NotNull(dialog);
                dialog.Measure(new Size(520, 560));
                dialog.Arrange(new Rect(0, 0, 520, 560));
                dialog.UpdateLayout();
            });
        }

        [WpfFact]
        public void MaintenanceDialog_Instantiate_SingleLink_ConfiguresRadioButtonsCorrectly()
        {
            WpfTestHelper.Run(() =>
            {
                var app = Application.Current ?? new Application();
                App.ApplyTheme("Light");

                var dev = new AvasDevice
                {
                    MacAddress = "00:14:95:11:22:33",
                    CompanionMac = "NONE",
                    LinkMode = "SINGLE"
                };
                var mockController = new MockMulticastController();
                var vm = new MaintenanceViewModel(dev, mockController);

                var dialog = new MaintenanceDialog(vm);
                Assert.NotNull(dialog);
                dialog.Show();
                dialog.UpdateLayout();

                var rbChip0 = (System.Windows.Controls.RadioButton)dialog.FindName("RbChip0");
                var rbChip1 = (System.Windows.Controls.RadioButton)dialog.FindName("RbChip1");
                var rbBoth = (System.Windows.Controls.RadioButton)dialog.FindName("RbBoth");

                Assert.NotNull(rbChip0);
                Assert.NotNull(rbChip1);
                Assert.NotNull(rbBoth);
                Assert.True(rbChip0.IsChecked);
                Assert.False(rbChip1.IsEnabled);
                Assert.False(rbBoth.IsEnabled);

                dialog.Close();
            });
        }
    }
}
