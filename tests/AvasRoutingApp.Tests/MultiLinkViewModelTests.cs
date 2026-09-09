using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.ViewModels;

namespace AvasRoutingApp.Tests
{
    public class MockMultiLinkService : IMultiLinkService
    {
        public List<MultiLinkInfo> PairsToReturn { get; } = new();
        public List<(string primary, string? comp, string mode)> ModeSwitches { get; } = new();
        public List<string> Reboots { get; } = new();
        public bool ModeSwitchResult = true;

        public Task<IReadOnlyList<MultiLinkInfo>> QueryMultiLinkPairsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<MultiLinkInfo>>(PairsToReturn.ToList());
        }

        public Task<bool> SetMultiLinkModeAsync(string primaryMac, string? companionMac, string targetMode, CancellationToken ct = default)
        {
            ModeSwitches.Add((primaryMac, companionMac, targetMode));
            return Task.FromResult(ModeSwitchResult);
        }

        public Task<bool> RebootDeviceAsync(string mac, CancellationToken ct = default)
        {
            Reboots.Add(mac);
            return Task.FromResult(true);
        }
    }

    public class MultiLinkViewModelTests
    {
        [Fact]
        public void MultiLinkCardViewModel_Properties_FormatCorrectly()
        {
            var mockService = new MockMultiLinkService();
            var vm = new MultiLinkCardViewModel(mockService)
            {
                PrimaryMac = "74fe488b07bb",
                PrimaryName = "TX-Floor1",
                PrimaryIp = "192.168.1.50",
                LinkMode = "DUAL",
                CompanionMac = "74FE488B07BC",
                CompanionName = "TX-Floor1-B",
                CompanionIsActive = true
            };

            Assert.Equal("DUAL-LINK (20G)", vm.LinkModeBadgeText);
            Assert.True(vm.IsDualLinkSelected);
            Assert.False(vm.IsSingleLinkSelected);
            Assert.True(vm.HasCompanion);
            Assert.Equal("ONLINE", vm.CompanionStatusText);
            Assert.Equal("TX-Floor1-B (74FE488B07BC)", vm.CompanionDisplayTitle);
            Assert.True(vm.CanSwitchMode);

            vm.LinkMode = "SINGLE";
            Assert.Equal("SINGLE-LINK (10G)", vm.LinkModeBadgeText);
            Assert.False(vm.IsDualLinkSelected);
            Assert.True(vm.IsSingleLinkSelected);

            vm.CompanionIsActive = false;
            Assert.Equal("OFFLINE", vm.CompanionStatusText);
        }

        [Fact]
        public void EncoderCardViewModel_LinkModeProperties_UpdateAndNotify()
        {
            var card = new EncoderCardViewModel
            {
                MacAddress = "74fe488b07bb",
                DeviceName = "TX-1",
                LinkMode = "SINGLE"
            };

            Assert.Equal("SINGLE", card.LinkModeBadgeText);
            Assert.False(card.IsDualLink);

            card.LinkMode = "DUAL";
            Assert.Equal("DUAL", card.LinkModeBadgeText);
            Assert.True(card.IsDualLink);

            card.LinkMode = "UNKNOWN";
            Assert.Equal("---", card.LinkModeBadgeText);
        }

        [Fact]
        public void MainViewModel_TabSwitching_TogglesStateCorrectly()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            var mockMultiLink = new MockMultiLinkService();
            var config = new ConfigService();

            var mainVm = new MainViewModel(mockDiscovery, mockController, config, mockMultiLink);

            // Default is Previews tab
            Assert.Equal(SidebarTab.Previews, mainVm.SelectedTab);
            Assert.True(mainVm.IsPreviewsTabSelected);
            Assert.False(mainVm.IsMultiLinkTabSelected);

            // Switch to MultiLink
            mainVm.SelectMultiLinkTabCommand.Execute(null);
            Assert.Equal(SidebarTab.MultiLink, mainVm.SelectedTab);
            Assert.False(mainVm.IsPreviewsTabSelected);
            Assert.True(mainVm.IsMultiLinkTabSelected);

            // Switch back to Previews
            mainVm.SelectPreviewsTabCommand.Execute(null);
            Assert.Equal(SidebarTab.Previews, mainVm.SelectedTab);
            Assert.True(mainVm.IsPreviewsTabSelected);
            Assert.False(mainVm.IsMultiLinkTabSelected);
        }

        [Fact]
        public async Task MainViewModel_ExpandSidebarAsync_PopulatesBothPreviewsAndMultiLinkCards()
        {
            var mockDiscovery = new MockDiscoveryService();
            mockDiscovery.DevicesToReturn.Add(new AvasDevice
            {
                MacAddress = "74fe488b07bb",
                DeviceName = "AVAS-223-TX0",
                IpAddress = "192.168.1.10",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true
            });

            var mockController = new MockMulticastController();
            var mockMultiLink = new MockMultiLinkService();
            mockMultiLink.PairsToReturn.Add(new MultiLinkInfo
            {
                PrimaryMac = "74fe488b07bb",
                PrimaryName = "AVAS-223-TX0",
                PrimaryIp = "192.168.1.10",
                LinkMode = "DUAL",
                CompanionMac = "74FE488B07BC",
                CompanionName = "AVAS-223-TX0-B",
                CompanionIsActive = true
            });

            var config = new ConfigService();
            var mainVm = new MainViewModel(mockDiscovery, mockController, config, mockMultiLink);

            await mainVm.ExpandSidebarAsync();

            // Check Preview Cards
            Assert.Single(mainVm.EncoderCards);
            var previewCard = mainVm.EncoderCards[0];
            Assert.Equal("74fe488b07bb", previewCard.MacAddress);
            Assert.Equal("DUAL", previewCard.LinkMode);
            Assert.Equal("DUAL", previewCard.LinkModeBadgeText);
            Assert.Equal("74FE488B07BC", previewCard.CompanionMac);
            Assert.True(previewCard.CompanionIsActive);

            // Check MultiLink Cards
            Assert.Single(mainVm.MultiLinkCards);
            var mlCard = mainVm.MultiLinkCards[0];
            Assert.Equal("74fe488b07bb", mlCard.PrimaryMac);
            Assert.Equal("DUAL", mlCard.LinkMode);
            Assert.Equal("ONLINE", mlCard.CompanionStatusText);
            Assert.True(mlCard.CompanionIsActive);

            // Collapse teardown
            await mainVm.CollapseSidebarAsync();
            Assert.Empty(mainVm.EncoderCards);
            Assert.Empty(mainVm.MultiLinkCards);
        }

        [Fact]
        public async Task MultiLinkCardViewModel_ExecuteModeSwitchAsync_CompanionOffline_UserCancels_AbortsCleanly()
        {
            var mockService = new MockMultiLinkService();
            var vm = new MultiLinkCardViewModel(mockService)
            {
                PrimaryMac = "74fe488b07bb",
                PrimaryName = "TX-Floor1",
                LinkMode = "SINGLE",
                CompanionMac = "74fe488b07bc",
                CompanionIsActive = false,
                ConfirmOfflineCompanionSwitch = (msg, title) => false // User clicks 'No'
            };

            await vm.ExecuteModeSwitchAsync("DUAL");

            // Mode switch should NOT be executed
            Assert.Empty(mockService.ModeSwitches);
            Assert.Equal("SINGLE", vm.LinkMode);
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsRebooting);
        }

        [Fact]
        public async Task MultiLinkCardViewModel_ExecuteModeSwitchAsync_CompanionOffline_UserConfirms_Proceeds()
        {
            var mockService = new MockMultiLinkService();
            var vm = new MultiLinkCardViewModel(mockService)
            {
                PrimaryMac = "74fe488b07bb",
                PrimaryName = "TX-Floor1",
                LinkMode = "SINGLE",
                CompanionMac = "74fe488b07bc",
                CompanionIsActive = false,
                ConfirmOfflineCompanionSwitch = (msg, title) => true // User clicks 'Yes'
            };

            await vm.ExecuteModeSwitchAsync("DUAL");

            // Mode switch should have been executed
            Assert.Single(mockService.ModeSwitches);
            Assert.Equal("DUAL", mockService.ModeSwitches[0].mode);
            Assert.Equal("DUAL", vm.LinkMode);
            Assert.True(vm.IsRebooting);

            vm.CancelCountdown();
        }

        [Fact]
        public async Task MultiLinkCardViewModel_ExecuteModeSwitchAsync_DeviceDisconnect_FailsGracefullyWithoutRebootCountdown()
        {
            var mockService = new MockMultiLinkService
            {
                ModeSwitchResult = false // Simulates device disconnected during mode toggle
            };

            bool modeSwitchCompletedFired = false;
            var vm = new MultiLinkCardViewModel(mockService)
            {
                PrimaryMac = "74fe488b07bb",
                PrimaryName = "TX-Floor1",
                LinkMode = "SINGLE",
                CompanionMac = "74fe488b07bc",
                CompanionIsActive = true
            };
            vm.ModeSwitchCompleted += card =>
            {
                modeSwitchCompletedFired = true;
                return Task.CompletedTask;
            };

            await vm.ExecuteModeSwitchAsync("DUAL");

            // Should have attempted mode switch
            Assert.Single(mockService.ModeSwitches);
            // Mode should remain SINGLE (not changed to DUAL)
            Assert.Equal("SINGLE", vm.LinkMode);
            // Should NOT be rebooting or busy
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsRebooting);
            Assert.Contains("failed", vm.RebootStatusMessage, StringComparison.OrdinalIgnoreCase);
            // Must have notified completion to allow stream recovery
            Assert.True(modeSwitchCompletedFired);
        }

        [Fact]
        public void MainViewModel_RapidTabSwitching_UnderStress_ZeroRaceConditions()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            var mockMultiLink = new MockMultiLinkService();
            var config = new ConfigService();

            var mainVm = new MainViewModel(mockDiscovery, mockController, config, mockMultiLink);

            // Rapidly alternate tabs 200 times across threads
            Parallel.For(0, 200, i =>
            {
                if (i % 2 == 0)
                {
                    mainVm.SelectedTab = SidebarTab.Previews;
                }
                else
                {
                    mainVm.SelectedTab = SidebarTab.MultiLink;
                }
            });

            // VM state should be consistent
            Assert.True(mainVm.IsPreviewsTabSelected || mainVm.IsMultiLinkTabSelected);
            Assert.NotEqual(mainVm.IsPreviewsTabSelected, mainVm.IsMultiLinkTabSelected);
        }
    }
}
