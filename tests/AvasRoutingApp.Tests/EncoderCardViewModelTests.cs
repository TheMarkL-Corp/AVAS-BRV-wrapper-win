using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.ViewModels;
using Xunit;

namespace AvasRoutingApp.Tests
{
    public class EncoderCardViewModelTests
    {
        [Fact]
        public void EncoderCardViewModel_WhenFamilyIsVega_ConfiguresBadgeAndMaintenanceExclusion()
        {
            var card = new EncoderCardViewModel();
            card.Family = DeviceFamily.Vega;

            Assert.True(card.IsVegaDevice);
            Assert.Equal("[VEGA-1100 (Test)]", card.BadgeText);
            Assert.False(card.IsMaintenanceAvailable);
        }

        [Fact]
        public void EncoderCardViewModel_WhenFamilyIsAvas_ConfiguresChip0BadgeAndMaintenanceAvailable()
        {
            var card = new EncoderCardViewModel();
            card.Family = DeviceFamily.Avas;

            Assert.False(card.IsVegaDevice);
            Assert.Equal("chip_0 (TX)", card.BadgeText);
            Assert.True(card.IsMaintenanceAvailable);
        }

        [Fact]
        public void EncoderCardViewModel_DefaultsToAvasDevice()
        {
            var card = new EncoderCardViewModel();

            Assert.Equal(DeviceFamily.Avas, card.Family);
            Assert.False(card.IsVegaDevice);
            Assert.Equal("chip_0 (TX)", card.BadgeText);
            Assert.True(card.IsMaintenanceAvailable);
        }
    }
}
