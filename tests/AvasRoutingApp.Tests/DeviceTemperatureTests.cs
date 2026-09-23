using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Xunit;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.ViewModels;

namespace AvasRoutingApp.Tests
{
    public class MockTemperatureService : ITemperatureService
    {
        public List<DeviceTemperatureInfo> DevicesToReturn { get; } = new();
        public int QueryCallCount { get; private set; }

        public Task<IReadOnlyList<DeviceTemperatureInfo>> QueryAllTemperaturesAsync(CancellationToken ct = default)
        {
            QueryCallCount++;
            return Task.FromResult<IReadOnlyList<DeviceTemperatureInfo>>(DevicesToReturn.ToList());
        }

        public Task<DeviceTemperatureInfo?> QueryDeviceTemperatureAsync(string primaryMac, string? companionMac = null, CancellationToken ct = default)
        {
            var dev = DevicesToReturn.FirstOrDefault(d => string.Equals(d.DeviceId, primaryMac, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(dev);
        }

        public void Dispose()
        {
        }
    }

    public class DeviceTemperatureTests
    {
        [Fact]
        public void DeviceTemperatureInfo_SingleLinkTx_PropertiesMatch()
        {
            var info = new DeviceTemperatureInfo
            {
                DeviceId = "00:11:22:33:44:01",
                DeviceName = "ENC-Floor1-Primary",
                DeviceType = "TX",
                LinkMode = "SINGLE",
                IpAddress = "192.168.1.51",
                Chip0Id = "00:11:22:33:44:01",
                Chip0SdvoeTemp = 58,
                Chip0VoipSdkTemp = 58
            };

            Assert.Equal("00:11:22:33:44:01", info.DeviceId);
            Assert.Equal("ENC-Floor1-Primary", info.DeviceName);
            Assert.Equal("TX", info.DeviceType);
            Assert.True(info.IsTransmitter);
            Assert.False(info.IsReceiver);
            Assert.Equal("SINGLE", info.LinkMode);
            Assert.False(info.IsDualLink);
            Assert.False(info.HasCompanion);
            Assert.Equal(58, info.Chip0SdvoeTemp);
            Assert.Equal(58, info.Chip0VoipSdkTemp);
            Assert.False(info.Chip0HasDiscrepancy);
            Assert.False(info.HasAnyDiscrepancy);
            Assert.False(info.IsCritical);
            Assert.False(info.IsWarning);
            Assert.Equal(58, info.MaxTemperature);
        }

        [Fact]
        public void DeviceTemperatureInfo_DualLinkCompanion_DiscrepancyAndPeak()
        {
            var info = new DeviceTemperatureInfo
            {
                DeviceId = "00:11:22:33:44:02",
                DeviceName = "ENC-4K-Dual",
                DeviceType = "TX",
                LinkMode = "DUAL",
                IpAddress = "192.168.1.52",
                Chip0Id = "00:11:22:33:44:02",
                Chip0SdvoeTemp = 65,
                Chip0VoipSdkTemp = 61, // Discrepancy > 3°C
                CompanionDeviceId = "00:11:22:33:44:03",
                CompanionName = "ENC-4K-Dual-B",
                Chip1Id = "00:11:22:33:44:03",
                Chip1SdvoeTemp = 59,
                Chip1VoipSdkTemp = 59
            };

            Assert.True(info.HasCompanion);
            Assert.True(info.IsDualLink);
            Assert.True(info.Chip0HasDiscrepancy);
            Assert.False(info.Chip1HasDiscrepancy);
            Assert.True(info.HasAnyDiscrepancy);
            Assert.Equal(65, info.MaxTemperature);
            Assert.True(info.IsWarning);
            Assert.False(info.IsCritical);
        }

        [Theory]
        [InlineData(55, 55, false, false)]  // Normal (<=60°C)
        [InlineData(60, 58, false, false)]  // Normal boundary (60°C)
        [InlineData(61, 61, true, false)]   // Warning lower boundary (61°C)
        [InlineData(70, 68, true, false)]   // Warning upper boundary (70°C)
        [InlineData(71, 65, false, true)]   // Critical (>70°C)
        [InlineData(85, 84, false, true)]   // Severe Critical
        public void DeviceTemperatureInfo_ThresholdEvaluation_CorrectStatus(int sdvoeTemp, int voipTemp, bool expectedWarning, bool expectedCritical)
        {
            var info = new DeviceTemperatureInfo
            {
                DeviceId = "00:11:22:33:44:10",
                DeviceType = "TX",
                Chip0SdvoeTemp = sdvoeTemp,
                Chip0VoipSdkTemp = voipTemp
            };

            Assert.Equal(expectedWarning, info.IsWarning);
            Assert.Equal(expectedCritical, info.IsCritical);
        }

        [Fact]
        public void DeviceTemperatureInfo_CompanionCriticalTemperature_TriggersCritical()
        {
            // Chip 0 is cool (50°C), but companion Chip 1 is overheating (74°C)
            var info = new DeviceTemperatureInfo
            {
                DeviceId = "00:11:22:33:44:20",
                DeviceType = "TX",
                Chip0SdvoeTemp = 50,
                Chip0VoipSdkTemp = 50,
                CompanionDeviceId = "00:11:22:33:44:21",
                Chip1SdvoeTemp = 74,
                Chip1VoipSdkTemp = 72
            };

            Assert.True(info.IsCritical);
            Assert.False(info.IsWarning);
            Assert.Equal(74, info.MaxTemperature);
        }

        [Fact]
        public void DeviceTemperatureCardViewModel_BoldRedOver70Degrees()
        {
            var info = new DeviceTemperatureInfo
            {
                DeviceId = "00:11:22:33:44:30",
                DeviceName = "Overheating-Encoder",
                DeviceType = "TX",
                Chip0SdvoeTemp = 75, // > 70°C
                Chip0VoipSdkTemp = 68
            };

            var vm = new DeviceTemperatureCardViewModel(info);

            // SDVoE temperature is > 70°C -> MUST be Bold and Red
            Assert.Equal(FontWeights.Bold, vm.Chip0SdvoeFontWeight);
            Assert.True(vm.Chip0IsCritical);
            Assert.True(vm.IsCritical);
            
            var sdvoeBrush = Assert.IsType<SolidColorBrush>(vm.Chip0SdvoeForeground);
            Assert.Equal(Color.FromRgb(0xEF, 0x44, 0x44), sdvoeBrush.Color);

            // VoIP temperature is 68°C (61-70°C) -> SemiBold and Amber
            Assert.Equal(FontWeights.SemiBold, vm.Chip0VoipFontWeight);
            var voipBrush = Assert.IsType<SolidColorBrush>(vm.Chip0VoipForeground);
            Assert.Equal(Color.FromRgb(0xF5, 0x9E, 0x0B), voipBrush.Color);

            // Card border turns red on critical
            var borderBrush = Assert.IsType<SolidColorBrush>(vm.CardBorderBrush);
            Assert.Equal(Color.FromRgb(0xEF, 0x44, 0x44), borderBrush.Color);
        }

        [Fact]
        public void DeviceTemperatureCardViewModel_NormalTemperature_GreenAndSemiBold()
        {
            var info = new DeviceTemperatureInfo
            {
                DeviceId = "00:11:22:33:44:31",
                DeviceName = "Cool-Decoder",
                DeviceType = "RX",
                Chip0SdvoeTemp = 48,
                Chip0VoipSdkTemp = 48
            };

            var vm = new DeviceTemperatureCardViewModel(info);

            Assert.Equal(FontWeights.SemiBold, vm.Chip0SdvoeFontWeight);
            Assert.Equal(FontWeights.SemiBold, vm.Chip0VoipFontWeight);
            Assert.False(vm.Chip0IsCritical);
            Assert.False(vm.IsCritical);

            var sdvoeBrush = Assert.IsType<SolidColorBrush>(vm.Chip0SdvoeForeground);
            Assert.Equal(Color.FromRgb(0x10, 0xB9, 0x81), sdvoeBrush.Color); // Normal Green
        }

        [Fact]
        public void DeviceTemperatureCardViewModel_DiscrepancyFormatting()
        {
            var info = new DeviceTemperatureInfo
            {
                DeviceId = "00:11:22:33:44:32",
                DeviceType = "TX",
                Chip0SdvoeTemp = 66,
                Chip0VoipSdkTemp = 61 // delta +5°C
            };

            var vm = new DeviceTemperatureCardViewModel(info);
            Assert.True(vm.Chip0HasDiscrepancy);
            Assert.Equal("Δ +5°C", vm.Chip0DiscrepancyText);

            // Discrepancy negative
            info.Chip0SdvoeTemp = 58;
            info.Chip0VoipSdkTemp = 62; // delta -4°C
            vm.Update(info);
            Assert.True(vm.Chip0HasDiscrepancy);
            Assert.Equal("Δ -4°C", vm.Chip0DiscrepancyText);

            // Exact match
            info.Chip0SdvoeTemp = 55;
            info.Chip0VoipSdkTemp = 55;
            vm.Update(info);
            Assert.False(vm.Chip0HasDiscrepancy);
            Assert.Equal("Match", vm.Chip0DiscrepancyText);

            // One missing source
            info.Chip0SdvoeTemp = 55;
            info.Chip0VoipSdkTemp = null;
            vm.Update(info);
            Assert.Equal("N/A", vm.Chip0DiscrepancyText);
        }

        [Fact]
        public void MainViewModel_TemperatureTab_SelectionAndSubFilters()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            var mockMultiLink = new MockMultiLinkService();
            var mockTempService = new MockTemperatureService();

            using var vm = new MainViewModel(
                discoveryService: mockDiscovery,
                multicastController: mockController,
                multiLinkService: mockMultiLink,
                temperatureService: mockTempService);

            // Default tab
            Assert.True(vm.IsPreviewsTabSelected);
            Assert.False(vm.IsTemperatureTabSelected);

            // Select Temperature Tab
            vm.SelectTemperatureTabCommand.Execute(null);
            Assert.True(vm.IsTemperatureTabSelected);
            Assert.False(vm.IsPreviewsTabSelected);
            Assert.False(vm.IsMultiLinkTabSelected);

            // Default filter is ALL
            Assert.True(vm.IsFilterAllSelected);
            Assert.False(vm.IsFilterTxSelected);
            Assert.False(vm.IsFilterRxSelected);
            Assert.True(vm.ShowTxSection);
            Assert.True(vm.ShowRxSection);

            // Filter TX only
            vm.SetFilterTxCommand.Execute(null);
            Assert.False(vm.IsFilterAllSelected);
            Assert.True(vm.IsFilterTxSelected);
            Assert.False(vm.IsFilterRxSelected);
            Assert.True(vm.ShowTxSection);
            Assert.False(vm.ShowRxSection);

            // Filter RX only
            vm.SetFilterRxCommand.Execute(null);
            Assert.False(vm.IsFilterAllSelected);
            Assert.False(vm.IsFilterTxSelected);
            Assert.True(vm.IsFilterRxSelected);
            Assert.False(vm.ShowTxSection);
            Assert.True(vm.ShowRxSection);

            // Reset to ALL
            vm.SetFilterAllCommand.Execute(null);
            Assert.True(vm.IsFilterAllSelected);
            Assert.True(vm.ShowTxSection);
            Assert.True(vm.ShowRxSection);
        }

        [Fact]
        public void MainViewModel_SectionExpandCollapse_TogglesCorrectly()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            var mockMultiLink = new MockMultiLinkService();
            var mockTempService = new MockTemperatureService();

            using var vm = new MainViewModel(
                discoveryService: mockDiscovery,
                multicastController: mockController,
                multiLinkService: mockMultiLink,
                temperatureService: mockTempService);

            Assert.True(vm.IsTxSectionExpanded);
            Assert.True(vm.IsRxSectionExpanded);

            vm.ToggleTxSectionCommand.Execute(null);
            Assert.False(vm.IsTxSectionExpanded);

            vm.ToggleRxSectionCommand.Execute(null);
            Assert.False(vm.IsRxSectionExpanded);

            vm.ToggleTxSectionCommand.Execute(null);
            Assert.True(vm.IsTxSectionExpanded);
        }

        [Fact]
        public async Task MainViewModel_RefreshTemperatures_PopulatesGroupsAndAlerts()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            var mockMultiLink = new MockMultiLinkService();
            var mockTempService = new MockTemperatureService();

            mockTempService.DevicesToReturn.AddRange(new[]
            {
                new DeviceTemperatureInfo
                {
                    DeviceId = "00:11:22:33:44:01",
                    DeviceName = "TX-Normal",
                    DeviceType = "TX",
                    LinkMode = "SINGLE",
                    Chip0SdvoeTemp = 52,
                    Chip0VoipSdkTemp = 52
                },
                new DeviceTemperatureInfo
                {
                    DeviceId = "00:11:22:33:44:02",
                    DeviceName = "TX-Hot",
                    DeviceType = "TX",
                    LinkMode = "DUAL",
                    Chip0SdvoeTemp = 74, // Over 70°C critical
                    Chip0VoipSdkTemp = 72,
                    CompanionDeviceId = "00:11:22:33:44:03",
                    Chip1SdvoeTemp = 68,
                    Chip1VoipSdkTemp = 68
                },
                new DeviceTemperatureInfo
                {
                    DeviceId = "00:11:22:33:44:10",
                    DeviceName = "RX-Display",
                    DeviceType = "RX",
                    LinkMode = "SINGLE",
                    Chip0SdvoeTemp = 49,
                    Chip0VoipSdkTemp = 49
                }
            });

            using var vm = new MainViewModel(
                discoveryService: mockDiscovery,
                multicastController: mockController,
                multiLinkService: mockMultiLink,
                temperatureService: mockTempService);

            await vm.RefreshTemperaturesAsync();

            Assert.Equal(3, vm.TotalTemperatureDeviceCount);
            Assert.Equal(2, vm.TxTemperatureCount);
            Assert.Equal(1, vm.RxTemperatureCount);
            Assert.Equal(1, vm.CriticalTemperatureCount);
            Assert.True(vm.HasAnyCriticalTemperature);

            // Filtered lists
            Assert.Equal(2, vm.FilteredTxCards.Count);
            Assert.Single(vm.FilteredRxCards);

            // Sub-filter to TX only
            vm.SelectedTemperatureFilter = "TX";
            Assert.Equal(2, vm.FilteredTxCards.Count);
            Assert.Empty(vm.FilteredRxCards);

            // Sub-filter to RX only
            vm.SelectedTemperatureFilter = "RX";
            Assert.Empty(vm.FilteredTxCards);
            Assert.Single(vm.FilteredRxCards);
        }

        [Fact]
        public async Task TemperatureService_RestQuery_ParsesEndpointsAndCompanions()
        {
            var handler = new MockHttpMessageHandler
            {
                Handler = req =>
                {
                    string url = req.RequestUri?.ToString() ?? "";
                    if (url.EndsWith("/api/device"))
                    {
                        string json = "{\"result\": [\"00:11:22:33:44:01\", \"00:11:22:33:44:02\"]}";
                        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")
                        });
                    }

                    if (url.Contains("/api/device/00:11:22:33:44:01"))
                    {
                        string json = @"{
                            ""result"": [{
                                ""device_id"": ""00:11:22:33:44:01"",
                                ""device_name"": ""ENC-Primary"",
                                ""identity"": { ""is_transmitter"": true, ""is_receiver"": false, ""chip_id"": 0 },
                                ""status"": { ""active"": true, ""temperature"": 68 },
                                ""nodes"": [
                                    { ""type"": ""MULTI_LINK_TRANSMITTER"", ""configuration"": { ""link_mode"": ""DUAL"" }, ""status"": { ""companions"": [{ ""device_id"": ""00:11:22:33:44:02"" }] } }
                                ]
                            }]
                        }";
                        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")
                        });
                    }

                    if (url.Contains("/api/device/00:11:22:33:44:02"))
                    {
                        string json = @"{
                            ""result"": [{
                                ""device_id"": ""00:11:22:33:44:02"",
                                ""device_name"": ""ENC-Companion"",
                                ""identity"": { ""is_transmitter"": true, ""is_receiver"": false, ""chip_id"": 1 },
                                ""status"": { ""active"": true, ""temperature"": 72 },
                                ""nodes"": []
                            }]
                        }";
                        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")
                        });
                    }

                    return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
                }
            };

            var httpClient = new System.Net.Http.HttpClient(handler);
            var configService = new ConfigService();
            using var tempService = new TemperatureService(configService, httpClient, enableVoipSdk: false);

            var results = await tempService.QueryAllTemperaturesAsync();

            Assert.Single(results); // Paired companion into single card
            var card = results[0];
            Assert.Equal("00:11:22:33:44:01", card.DeviceId);
            Assert.Equal("ENC-Primary", card.DeviceName);
            Assert.True(card.HasCompanion);
            Assert.Equal("00:11:22:33:44:02", card.CompanionDeviceId);
            Assert.Equal(68, card.Chip0SdvoeTemp);
            Assert.Equal(72, card.Chip1SdvoeTemp);
            Assert.True(card.IsCritical); // Chip 1 at 72°C > 70°C triggers critical
        }

        [Fact]
        public async Task TemperatureService_NetworkFailure_DegradesGracefully()
        {
            var handler = new MockHttpMessageHandler
            {
                Handler = req => throw new System.Net.Http.HttpRequestException("Connection refused")
            };

            var httpClient = new System.Net.Http.HttpClient(handler);
            var configService = new ConfigService();
            using var tempService = new TemperatureService(configService, httpClient, enableVoipSdk: false);

            var results = await tempService.QueryAllTemperaturesAsync();
            Assert.NotNull(results);
            Assert.Empty(results); // Does not throw, returns empty gracefully
        }
    }
}
