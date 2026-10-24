using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.ViewModels;

namespace AvasRoutingApp.Tests
{
    public class AdvancedSettingsLockTests
    {
        [Fact]
        public void DefaultState_IsLockedAndTabIsInvisible()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            using var vm = new MainViewModel(mockDiscovery, mockController);

            Assert.False(vm.IsAdvancedUnlocked);
            Assert.False(vm.IsAdvancedTabVisible);
            Assert.Equal(SidebarTab.Previews, vm.SelectedTab);
            Assert.True(vm.IsPreviewsTabSelected);
            Assert.False(vm.IsAdvancedTabSelected);
        }

        [Theory]
        [InlineData("00000000")]
        [InlineData(" 00000000 ")]
        [InlineData("00000000\t")]
        public void UnlockAdvancedSettings_WithValidPin_UnlocksAndShowsTab(string pin)
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            using var vm = new MainViewModel(mockDiscovery, mockController);

            bool result = vm.UnlockAdvancedSettings(pin);

            Assert.True(result);
            Assert.True(vm.IsAdvancedUnlocked);
            Assert.True(vm.IsAdvancedTabVisible);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(null)]
        [InlineData("12345678")]
        [InlineData("0000000")]
        [InlineData("000000000")]
        [InlineData("abcdefgh")]
        public void UnlockAdvancedSettings_WithInvalidPin_FailsAndRemainsLocked(string? pin)
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            using var vm = new MainViewModel(mockDiscovery, mockController);

            bool result = vm.UnlockAdvancedSettings(pin);

            Assert.False(result);
            Assert.False(vm.IsAdvancedUnlocked);
            Assert.False(vm.IsAdvancedTabVisible);
        }

        [Fact]
        public void LockAdvancedSettings_WhenUnlockedAndOnAdvancedTab_RelocksAndSwitchesToPreviews()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            using var vm = new MainViewModel(mockDiscovery, mockController);

            // 1. Unlock
            Assert.True(vm.UnlockAdvancedSettings("00000000"));
            Assert.True(vm.IsAdvancedUnlocked);

            // 2. Select Advanced Tab
            vm.SelectAdvancedTabCommand.Execute(null);
            Assert.Equal(SidebarTab.Advanced, vm.SelectedTab);
            Assert.True(vm.IsAdvancedTabSelected);

            // 3. Lock
            vm.LockAdvancedSettings();

            Assert.False(vm.IsAdvancedUnlocked);
            Assert.False(vm.IsAdvancedTabVisible);
            Assert.Equal(SidebarTab.Previews, vm.SelectedTab);
            Assert.True(vm.IsPreviewsTabSelected);
            Assert.False(vm.IsAdvancedTabSelected);
        }

        [Fact]
        public void LockAdvancedSettings_WhenOnMultiLinkTab_PreservesMultiLinkTab()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            using var vm = new MainViewModel(mockDiscovery, mockController);

            vm.UnlockAdvancedSettings("00000000");
            vm.SelectMultiLinkTabCommand.Execute(null);
            Assert.Equal(SidebarTab.MultiLink, vm.SelectedTab);

            vm.LockAdvancedSettings();

            Assert.False(vm.IsAdvancedUnlocked);
            Assert.False(vm.IsAdvancedTabVisible);
            Assert.Equal(SidebarTab.MultiLink, vm.SelectedTab);
            Assert.True(vm.IsMultiLinkTabSelected);
        }

        [Fact]
        public void LockAdvancedSettingsCommand_ExecutesLockAdvancedSettings()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            using var vm = new MainViewModel(mockDiscovery, mockController);

            vm.UnlockAdvancedSettings("00000000");
            vm.SelectAdvancedTabCommand.Execute(null);
            Assert.True(vm.IsAdvancedTabSelected);

            vm.LockAdvancedSettingsCommand.Execute(null);

            Assert.False(vm.IsAdvancedUnlocked);
            Assert.Equal(SidebarTab.Previews, vm.SelectedTab);
        }

        [Fact]
        public void AdvancedSubTab_Switching_TogglesThermalsAndDiagnosticsActive()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            using var vm = new MainViewModel(mockDiscovery, mockController);

            vm.UnlockAdvancedSettings("00000000");
            vm.SelectAdvancedTabCommand.Execute(null);

            // Default sub-tab is Thermals
            Assert.True(vm.IsAdvancedThermalsSelected);
            Assert.False(vm.IsAdvancedDiagnosticsSelected);

            // Switch to Diagnostics
            vm.SelectAdvancedDiagnosticsCommand.Execute(null);
            Assert.False(vm.IsAdvancedThermalsSelected);
            Assert.True(vm.IsAdvancedDiagnosticsSelected);
            Assert.Equal(SidebarTab.Advanced, vm.SelectedTab);

            // Switch back to Thermals
            vm.SelectAdvancedThermalsCommand.Execute(null);
            Assert.True(vm.IsAdvancedThermalsSelected);
            Assert.False(vm.IsAdvancedDiagnosticsSelected);
            Assert.Equal(SidebarTab.Advanced, vm.SelectedTab);
        }

        [Fact]
        public void BackwardCompatibility_LegacyTemperatureAndDiagnosticsTabs_MapToAdvanced()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            using var vm = new MainViewModel(mockDiscovery, mockController);

            vm.SelectTemperatureTabCommand.Execute(null);
            Assert.Equal(SidebarTab.Temperature, vm.SelectedTab);
            Assert.True(vm.IsTemperatureTabSelected);
            Assert.True(vm.IsAdvancedTabSelected);
            Assert.True(vm.IsAdvancedThermalsSelected);
            Assert.False(vm.IsAdvancedDiagnosticsSelected);

            vm.SelectDiagnosticsTabCommand.Execute(null);
            Assert.Equal(SidebarTab.Diagnostics, vm.SelectedTab);
            Assert.True(vm.IsDiagnosticsTabSelected);
            Assert.True(vm.IsAdvancedTabSelected);
            Assert.False(vm.IsAdvancedThermalsSelected);
            Assert.True(vm.IsAdvancedDiagnosticsSelected);
        }
    }
}
