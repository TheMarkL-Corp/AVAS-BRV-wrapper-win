using System.Windows.Media;
using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.ViewModels;
using Xunit;

namespace AvasRoutingApp.Tests
{
    public class EncoderCardViewModelTests
    {
        [Fact]
        public void EncoderCardViewModel_LinkModeBadgeText_FormatsSingleAndDualLink()
        {
            var card = new EncoderCardViewModel();

            // Default
            Assert.Equal("UNKNOWN", card.LinkMode);
            Assert.Equal("Unknown Link", card.LinkModeBadgeText);
            Assert.False(card.ShowLinkModeBadge);
            Assert.False(card.IsDualLink);

            // Dual Link
            card.LinkMode = "DUAL";
            Assert.Equal("Dual Link", card.LinkModeBadgeText);
            Assert.True(card.ShowLinkModeBadge);
            Assert.True(card.IsDualLink);
            Assert.Equal(Color.FromRgb(0x25, 0x63, 0xEB), card.LinkModeBrush.Color);

            // Single Link
            card.LinkMode = "SINGLE";
            Assert.Equal("Single Link", card.LinkModeBadgeText);
            Assert.True(card.ShowLinkModeBadge);
            Assert.False(card.IsDualLink);
            Assert.Equal(Color.FromRgb(0x47, 0x55, 0x69), card.LinkModeBrush.Color);

            // Case Insensitive
            card.LinkMode = "dual";
            Assert.Equal("Dual Link", card.LinkModeBadgeText);
            Assert.True(card.ShowLinkModeBadge);
            Assert.True(card.IsDualLink);

            card.LinkMode = "single";
            Assert.Equal("Single Link", card.LinkModeBadgeText);
            Assert.True(card.ShowLinkModeBadge);

            // Unknown
            card.LinkMode = "NONE";
            Assert.Equal("Unknown Link", card.LinkModeBadgeText);
            Assert.False(card.ShowLinkModeBadge);
            Assert.Equal(Color.FromRgb(0x33, 0x41, 0x55), card.LinkModeBrush.Color);
        }

        [Fact]
        public void EncoderCardViewModel_MinimalistFooterTelemetry_SetsPropertiesCorrectly()
        {
            var card = new EncoderCardViewModel
            {
                UnicastIp = "192.168.1.100",
                MacAddress = "00:11:22:33:44:55",
                CurrentFps = 60.0
            };

            Assert.Equal("192.168.1.100", card.UnicastIp);
            Assert.Equal("00:11:22:33:44:55", card.MacAddress);
            Assert.Equal("60.0 FPS", card.FpsDisplay);
            Assert.True(card.IsFpsHealthy);
            Assert.NotNull(card.CopyMacCommand);
        }

        [Fact]
        public void EncoderCardViewModel_WhenFamilyIsGenericSdvoe_ConfiguresBadgeAndMaintenanceExclusion()
        {
            var card = new EncoderCardViewModel();
            card.Family = DeviceFamily.GenericSdvoe;

#pragma warning disable CS0618 // Type or member is obsolete
            Assert.Equal("chip_0 (TX)", card.BadgeText);
            Assert.False(card.IsMaintenanceAvailable);
            Assert.False(card.ShowActivationBadge);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        [Fact]
        public void EncoderCardViewModel_WhenFamilyIsAvas_ConfiguresChip0BadgeAndMaintenanceAvailable()
        {
            var card = new EncoderCardViewModel();
            card.Family = DeviceFamily.Avas;

#pragma warning disable CS0618 // Type or member is obsolete
            Assert.Equal("chip_0 (TX)", card.BadgeText);
            Assert.True(card.IsMaintenanceAvailable);
            Assert.True(card.ShowActivationBadge);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        [Fact]
        public void EncoderCardViewModel_DefaultsToAvasDevice()
        {
            var card = new EncoderCardViewModel();

            Assert.Equal(DeviceFamily.Avas, card.Family);
#pragma warning disable CS0618 // Type or member is obsolete
            Assert.Equal("chip_0 (TX)", card.BadgeText);
            Assert.True(card.IsMaintenanceAvailable);
            Assert.True(card.ShowActivationBadge);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        [Fact]
        public void SingleLink_WithCompanionMac_CanSelectChip1_ReturnsTrue()
        {
            var vm = new EncoderCardViewModel
            {
                MacAddress = "74fe4887fdf4",
                LinkMode = "SINGLE",
                CompanionMac = "74fe4887fdf5"
            };

            Assert.True(vm.CanSelectChip1);
            Assert.Equal("74fe4887fdf4", vm.ActiveStreamingMac);

            vm.SelectChip1Command.Execute(null);
            Assert.Equal(1, vm.SelectedChipIndex);
            Assert.Equal("74fe4887fdf5", vm.ActiveStreamingMac);

            vm.SelectChip0Command.Execute(null);
            Assert.Equal(0, vm.SelectedChipIndex);
            Assert.Equal("74fe4887fdf4", vm.ActiveStreamingMac);
        }

        [Fact]
        public void DualLink_CannotSelectChip1()
        {
            var vm = new EncoderCardViewModel
            {
                MacAddress = "74fe4887fdf4",
                LinkMode = "DUAL",
                CompanionMac = "74fe4887fdf5"
            };

            Assert.False(vm.CanSelectChip1);
            Assert.Equal("74fe4887fdf4", vm.ActiveStreamingMac);
        }

        [Fact]
        public void EncoderCardViewModel_ChipSelection_TriggersOnChipSelectionChanged()
        {
            var vm = new EncoderCardViewModel
            {
                MacAddress = "74fe4887fdf4",
                LinkMode = "SINGLE",
                CompanionMac = "74fe4887fdf5"
            };

            int callbackCount = 0;
            int selectedIndex = -1;
            vm.OnChipSelectionChanged = (card, idx) =>
            {
                callbackCount++;
                selectedIndex = idx;
                return System.Threading.Tasks.Task.CompletedTask;
            };

            Assert.True(vm.IsChip0Selected);
            Assert.False(vm.IsChip1Selected);

            vm.SelectChip1Command.Execute(null);
            Assert.Equal(1, vm.SelectedChipIndex);
            Assert.False(vm.IsChip0Selected);
            Assert.True(vm.IsChip1Selected);
            Assert.Equal(1, callbackCount);
            Assert.Equal(1, selectedIndex);

            // Re-selecting same chip should not trigger callback again
            vm.SelectChip1Command.Execute(null);
            Assert.Equal(1, callbackCount);

            // Selecting chip 0
            vm.SelectChip0Command.Execute(null);
            Assert.Equal(0, vm.SelectedChipIndex);
            Assert.True(vm.IsChip0Selected);
            Assert.False(vm.IsChip1Selected);
            Assert.Equal(2, callbackCount);
            Assert.Equal(0, selectedIndex);
        }

        [Theory]
        [InlineData("NONE")]
        [InlineData("")]
        [InlineData(null)]
        public void SingleLink_InvalidCompanionMac_CannotSelectChip1(string? companionMac)
        {
            var vm = new EncoderCardViewModel
            {
                MacAddress = "74fe4887fdf4",
                LinkMode = "SINGLE",
                CompanionMac = companionMac!
            };

            Assert.False(vm.CanSelectChip1);
            Assert.Equal("74fe4887fdf4", vm.ActiveStreamingMac);
        }
    }
}
