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

            public string? AllocateMulticastIp(string macAddress) => null;
            public void ReleaseMulticastIp(string macAddress) { }
            public Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, int port, CancellationToken ct = default) => Task.FromResult(true);
            public Task<bool> StopPreviewStreamAsync(string macAddress, CancellationToken ct = default) => Task.FromResult(true);
            public Task<bool> StartVegaPreviewStreamAsync(string macAddress, string multicastIp, int port, CancellationToken ct = default) => Task.FromResult(true);
            public Task<bool> StopVegaPreviewStreamAsync(string macAddress, CancellationToken ct = default) => Task.FromResult(true);

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
    }
}
