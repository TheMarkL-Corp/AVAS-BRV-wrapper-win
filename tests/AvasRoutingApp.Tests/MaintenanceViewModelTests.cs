using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.ViewModels;
using Xunit;

namespace AvasRoutingApp.Tests
{
    public class MaintenanceViewModelTests
    {
        private class MockDiagnosticsController : IMulticastController
        {
            public List<string> AvpResetCalls { get; } = new();
            public List<string> McuResetCalls { get; } = new();
            public List<string> RebootCalls { get; } = new();
            public List<string> McuRebootCalls { get; } = new();

            public string? AllocateMulticastIp(string macAddress) => null;
            public void ReleaseMulticastIp(string macAddress) { }
            public Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, int port, string linkMode = "UNKNOWN", int chipIndex = 0, CancellationToken ct = default) => Task.FromResult(true);
            public Task<bool> StopPreviewStreamAsync(string macAddress, string linkMode = "UNKNOWN", int chipIndex = 0, CancellationToken ct = default) => Task.FromResult(true);

            public Task<bool> FactoryResetAvpAsync(AvasDevice device, string targetMac, CancellationToken ct = default)
            {
                AvpResetCalls.Add(targetMac);
                return Task.FromResult(true);
            }

            public Task<bool> FactoryResetMcuAsync(AvasDevice device, CancellationToken ct = default)
            {
                McuResetCalls.Add(device.MacAddress);
                return Task.FromResult(true);
            }

            public Task<bool> RebootChipAsync(AvasDevice device, string targetMac, CancellationToken ct = default)
            {
                RebootCalls.Add(targetMac);
                return Task.FromResult(true);
            }

            public Task<bool> RebootMcuAsync(AvasDevice device, CancellationToken ct = default)
            {
                McuRebootCalls.Add(device.MacAddress);
                return Task.FromResult(true);
            }
        }

        [Fact]
        public void Initialization_SingleLinkDevice_DisablesChip1Selection()
        {
            var controller = new MockDiagnosticsController();
            var device = new AvasDevice
            {
                MacAddress = "00:14:95:11:22:33",
                CompanionMac = "NONE",
                LinkMode = "SINGLE"
            };

            var vm = new MaintenanceViewModel(device, controller);

            Assert.Equal(TargetChipSelection.Chip0, vm.SelectedTarget);
            Assert.False(vm.IsChip1Available);
            Assert.Equal("00:14:95:11:22:33", vm.Chip0Mac);
            Assert.Equal("N/A", vm.Chip1Mac);
        }

        [Fact]
        public void Initialization_DualLinkDevice_EnablesChip1Selection()
        {
            var controller = new MockDiagnosticsController();
            var device = new AvasDevice
            {
                MacAddress = "00:14:95:11:22:33",
                CompanionMac = "00:14:95:11:22:34",
                LinkMode = "DUAL"
            };

            var vm = new MaintenanceViewModel(device, controller);

            Assert.True(vm.IsChip1Available);
            Assert.Equal("00:14:95:11:22:33", vm.Chip0Mac);
            Assert.Equal("00:14:95:11:22:34", vm.Chip1Mac);
        }

        [Fact]
        public async Task ExecuteFactoryReset_TargetBoth_ResetsBothChipsSequentially()
        {
            var controller = new MockDiagnosticsController();
            var device = new AvasDevice
            {
                MacAddress = "00:14:95:11:22:33",
                CompanionMac = "00:14:95:11:22:34",
                LinkMode = "DUAL"
            };

            var vm = new MaintenanceViewModel(device, controller)
            {
                ConfirmActionCallback = () => true // User accepts prompt
            };
            vm.SelectedTarget = TargetChipSelection.Both;

            await vm.ExecuteFactoryResetAvpAsync();

            Assert.Equal(2, controller.AvpResetCalls.Count);
            Assert.Equal("00:14:95:11:22:33", controller.AvpResetCalls[0]);
            Assert.Equal("00:14:95:11:22:34", controller.AvpResetCalls[1]);
        }

        [Fact]
        public async Task ExecuteFactoryResetMcu_AlwaysRoutesThroughChip0Mac()
        {
            var controller = new MockDiagnosticsController();
            var device = new AvasDevice
            {
                MacAddress = "00:14:95:11:22:33",
                CompanionMac = "00:14:95:11:22:34",
                LinkMode = "DUAL"
            };

            var vm = new MaintenanceViewModel(device, controller)
            {
                ConfirmActionCallback = () => true
            };
            vm.SelectedTarget = TargetChipSelection.Chip1; // Even if user selected Chip 1

            await vm.ExecuteFactoryResetMcuAsync();

            Assert.Single(controller.McuResetCalls);
            Assert.Equal("00:14:95:11:22:33", controller.McuResetCalls[0]);
        }

        [Fact]
        public async Task ExecuteReboot_TargetChip1_RebootsOnlyChip1()
        {
            var controller = new MockDiagnosticsController();
            var device = new AvasDevice
            {
                MacAddress = "00:14:95:11:22:33",
                CompanionMac = "00:14:95:11:22:34",
                LinkMode = "DUAL"
            };

            var vm = new MaintenanceViewModel(device, controller)
            {
                ConfirmActionCallback = () => true
            };
            vm.SelectedTarget = TargetChipSelection.Chip1;

            await vm.ExecuteRebootAsync();

            Assert.Single(controller.RebootCalls);
            Assert.Equal("00:14:95:11:22:34", controller.RebootCalls[0]);
        }

        [Fact]
        public void MaintenanceCardViewModel_AvasTxDevice_EnablesDiagnosticsAndSetsTxBadge()
        {
            var device = new AvasDevice
            {
                MacAddress = "00:14:95:11:22:33",
                DeviceName = "TX_UNIT_1",
                VendorId = 105,
                ProductId = 81,
                IsTransmitter = true,
                IsReceiver = false,
                LinkMode = "DUAL",
                CompanionMac = "00:14:95:11:22:34"
            };

            var card = new MaintenanceCardViewModel(device);

            Assert.Equal("TX", card.DeviceTypeBadge);
            Assert.True(card.IsTransmitter);
            Assert.False(card.IsReceiver);
            Assert.True(card.IsMaintenanceAvailable);
            Assert.True(card.OpenDiagnosticsCommand.CanExecute(null));

            bool actionFired = false;
            card.OpenDiagnosticsAction = _ => actionFired = true;
            card.OpenDiagnosticsCommand.Execute(null);
            Assert.True(actionFired);
        }

        [Fact]
        public void MaintenanceCardViewModel_AvasRxDevice_EnablesDiagnosticsAndSetsRxBadge()
        {
            var device = new AvasDevice
            {
                MacAddress = "00:14:95:AA:BB:CC",
                DeviceName = "RX_UNIT_1",
                VendorId = 105,
                ProductId = 81,
                IsTransmitter = false,
                IsReceiver = true,
                LinkMode = "SINGLE",
                CompanionMac = "NONE"
            };

            var card = new MaintenanceCardViewModel(device);

            Assert.Equal("RX", card.DeviceTypeBadge);
            Assert.False(card.IsTransmitter);
            Assert.True(card.IsReceiver);
            Assert.True(card.IsMaintenanceAvailable);
            Assert.True(card.OpenDiagnosticsCommand.CanExecute(null));

            bool actionFired = false;
            card.OpenDiagnosticsAction = _ => actionFired = true;
            card.OpenDiagnosticsCommand.Execute(null);
            Assert.True(actionFired);
        }

        [Fact]
        public void MaintenanceCardViewModel_NonAvasDevice_DisablesDiagnostics()
        {
            var device = new AvasDevice
            {
                MacAddress = "00:16:C0:58:9C:1E",
                DeviceName = "Generic_Endpoint",
                VendorId = 999,
                ProductId = 1,
                IsTransmitter = true,
                IsReceiver = false
            };

            var card = new MaintenanceCardViewModel(device);

            Assert.False(card.IsMaintenanceAvailable);
            Assert.False(card.OpenDiagnosticsCommand.CanExecute(null));

            bool actionFired = false;
            card.OpenDiagnosticsAction = _ => actionFired = true;
            card.OpenDiagnosticsCommand.Execute(null);
            Assert.False(actionFired);
        }

        [Fact]
        public void MaintenanceViewModel_DevicePicker_SwitchesActiveDevice()
        {
            var controller = new MockDiagnosticsController();
            var devTx = new AvasDevice
            {
                MacAddress = "00:14:95:11:22:33",
                DeviceName = "TX_UNIT",
                VendorId = 105,
                IsTransmitter = true,
                IsReceiver = false,
                LinkMode = "SINGLE"
            };
            var devRx = new AvasDevice
            {
                MacAddress = "00:14:95:AA:BB:CC",
                DeviceName = "RX_UNIT",
                VendorId = 105,
                IsTransmitter = false,
                IsReceiver = true,
                LinkMode = "DUAL",
                CompanionMac = "00:14:95:AA:BB:CD"
            };

            var available = new List<AvasDevice> { devTx, devRx };
            var vm = new MaintenanceViewModel(devTx, controller, available);

            Assert.True(vm.HasMultipleDevices);
            Assert.Equal("TX_UNIT", vm.DeviceName);
            Assert.Equal("TX", vm.DeviceTypeBadge);
            Assert.False(vm.IsChip1Available);

            // Switch to RX device
            vm.SelectedDevice = devRx;

            Assert.Equal("RX_UNIT", vm.DeviceName);
            Assert.Equal("RX", vm.DeviceTypeBadge);
            Assert.Equal("00:14:95:AA:BB:CC", vm.Chip0Mac);
            Assert.Equal("00:14:95:AA:BB:CD", vm.Chip1Mac);
            Assert.True(vm.IsChip1Available);
            Assert.Contains("Target device switched to RX_UNIT", vm.StatusLog);
        }
    [Fact]
        public async Task ExecuteRebootMcu_AlwaysRoutesThroughChip0Mac()
        {
            var controller = new MockDiagnosticsController();
            var device = new AvasDevice
            {
                MacAddress = "00:14:95:11:22:33",
                CompanionMac = "00:14:95:11:22:34",
                LinkMode = "DUAL"
            };

            var vm = new MaintenanceViewModel(device, controller)
            {
                ConfirmActionCallback = () => true
            };
            vm.SelectedTarget = TargetChipSelection.Chip1; // Even if user selected Chip 1

            await vm.ExecuteRebootMcuAsync();

            Assert.Single(controller.McuRebootCalls);
            Assert.Equal("00:14:95:11:22:33", controller.McuRebootCalls[0]);
            Assert.Contains("MCU reboot SUCCESS", vm.StatusLog);
        }

        [Fact]
        public async Task ExecuteRebootMcu_WhenUserAborts_DoesNotCallController()
        {
            var controller = new MockDiagnosticsController();
            var device = new AvasDevice
            {
                MacAddress = "00:14:95:11:22:33",
                LinkMode = "SINGLE"
            };

            var vm = new MaintenanceViewModel(device, controller)
            {
                ConfirmActionCallback = () => false
            };

            await vm.ExecuteRebootMcuAsync();

            Assert.Empty(controller.McuRebootCalls);
            Assert.Contains("MCU Reboot aborted by user", vm.StatusLog);
        }
    }
}