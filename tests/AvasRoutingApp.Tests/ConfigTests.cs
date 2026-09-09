using System;
using System.IO;
using System.Text.Json;
using Xunit;
using AvasRoutingApp.Configuration;

namespace AvasRoutingApp.Tests
{
    public class ConfigTests : IDisposable
    {
        private readonly string _testDirectory;

        public ConfigTests()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "AvasRoutingApp_Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDirectory);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDirectory))
                {
                    Directory.Delete(_testDirectory, true);
                }
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }

        [Fact]
        public void DefaultValues_MatchProjectRequirements()
        {
            var config = new AppConfig();

            Assert.Equal("http://localhost:80", config.BlueRiverUrl);
            Assert.Equal("127.0.0.1", config.ControlServerIp);
            Assert.Equal(8090, config.RestPort);
            Assert.Equal(6970, config.TelnetPort);
            Assert.Equal("224.1.3.1", config.MulticastStartIp);
            Assert.Equal("224.1.3.225", config.MulticastEndIp);
            Assert.Equal(5000, config.BasePort);
            Assert.Equal("", config.LocalNetworkInterfaceIp);
            Assert.Equal("Light", config.Theme);
            Assert.Equal(400.0, config.SidebarWidth);
        }

        [Fact]
        public void Clone_ProducesIndependentCopy()
        {
            var original = new AppConfig
            {
                BlueRiverUrl = "http://192.168.1.50:8080",
                ControlServerIp = "192.168.1.10",
                RestPort = 9200,
                TelnetPort = 6971,
                MulticastStartIp = "224.1.2.1",
                MulticastEndIp = "224.1.2.100",
                BasePort = 7000,
                LocalNetworkInterfaceIp = "192.168.1.200",
                Theme = "Light",
                SidebarWidth = 480.0
            };

            var clone = original.Clone();

            Assert.Equal(original.BlueRiverUrl, clone.BlueRiverUrl);
            Assert.Equal(original.ControlServerIp, clone.ControlServerIp);
            Assert.Equal(original.RestPort, clone.RestPort);
            Assert.Equal(original.TelnetPort, clone.TelnetPort);
            Assert.Equal(original.MulticastStartIp, clone.MulticastStartIp);
            Assert.Equal(original.MulticastEndIp, clone.MulticastEndIp);
            Assert.Equal(original.BasePort, clone.BasePort);
            Assert.Equal(original.LocalNetworkInterfaceIp, clone.LocalNetworkInterfaceIp);
            Assert.Equal(original.Theme, clone.Theme);
            Assert.Equal(original.SidebarWidth, clone.SidebarWidth);

            // Mutate clone and assert original remains unchanged
            clone.BlueRiverUrl = "http://modified.local";
            clone.RestPort = 9999;
            clone.Theme = "Dark";
            clone.SidebarWidth = 350.0;
            Assert.Equal("http://192.168.1.50:8080", original.BlueRiverUrl);
            Assert.Equal(9200, original.RestPort);
            Assert.Equal("Light", original.Theme);
            Assert.Equal(480.0, original.SidebarWidth);
        }

        [Fact]
        public void Serialization_Deserialization_PreservesAllFields()
        {
            var expected = new AppConfig
            {
                BlueRiverUrl = "http://10.0.0.5:4000",
                ControlServerIp = "10.0.0.1",
                RestPort = 8081,
                TelnetPort = 6972,
                MulticastStartIp = "225.1.1.1",
                MulticastEndIp = "225.1.2.200",
                BasePort = 6800,
                LocalNetworkInterfaceIp = "10.0.0.100",
                Theme = "Light",
                SidebarWidth = 425.0
            };

            string json = JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true });
            var actual = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.NotNull(actual);
            Assert.Equal(expected.BlueRiverUrl, actual.BlueRiverUrl);
            Assert.Equal(expected.ControlServerIp, actual.ControlServerIp);
            Assert.Equal(expected.RestPort, actual.RestPort);
            Assert.Equal(expected.TelnetPort, actual.TelnetPort);
            Assert.Equal(expected.MulticastStartIp, actual.MulticastStartIp);
            Assert.Equal(expected.MulticastEndIp, actual.MulticastEndIp);
            Assert.Equal(expected.BasePort, actual.BasePort);
            Assert.Equal(expected.LocalNetworkInterfaceIp, actual.LocalNetworkInterfaceIp);
            Assert.Equal(expected.Theme, actual.Theme);
            Assert.Equal(expected.SidebarWidth, actual.SidebarWidth);
        }

        [Fact]
        public void ConfigService_MissingFile_CreatesDefaultAndPersists()
        {
            string configPath = Path.Combine(_testDirectory, "subdir", "appsettings.json");
            Assert.False(File.Exists(configPath));

            var service = new ConfigService(configPath);
            var current = service.Current;

            Assert.NotNull(current);
            Assert.Equal("http://localhost:80", current.BlueRiverUrl);
            Assert.True(File.Exists(configPath), "Config file should be created on initialization if missing.");
        }

        [Fact]
        public void ConfigService_Save_AtomicPersistenceAndReload()
        {
            string configPath = Path.Combine(_testDirectory, "appsettings.json");
            var service = new ConfigService(configPath);

            var updated = new AppConfig
            {
                BlueRiverUrl = "http://192.168.1.2:3000",
                ControlServerIp = "192.168.1.100",
                RestPort = 9090,
                TelnetPort = 6975,
                MulticastStartIp = "224.2.0.1",
                MulticastEndIp = "224.2.0.50",
                BasePort = 7000,
                LocalNetworkInterfaceIp = "192.168.1.50"
            };

            service.Save(updated);

            // Assert file exists and no temporary files left behind
            Assert.True(File.Exists(configPath));
            var tempFiles = Directory.GetFiles(_testDirectory, "*tmp*");
            Assert.Empty(tempFiles);

            // Instantiate a new service pointing to the same file to verify persistence
            var service2 = new ConfigService(configPath);
            Assert.Equal("http://192.168.1.2:3000", service2.Current.BlueRiverUrl);
            Assert.Equal("192.168.1.100", service2.Current.ControlServerIp);
            Assert.Equal(9090, service2.Current.RestPort);
            Assert.Equal(6975, service2.Current.TelnetPort);
            Assert.Equal("224.2.0.1", service2.Current.MulticastStartIp);
            Assert.Equal("224.2.0.50", service2.Current.MulticastEndIp);
            Assert.Equal(7000, service2.Current.BasePort);
            Assert.Equal("192.168.1.50", service2.Current.LocalNetworkInterfaceIp);
        }

        [Fact]
        public void ConfigService_CorruptedJson_FallsBackToDefaultsGracefully()
        {
            string configPath = Path.Combine(_testDirectory, "appsettings.json");
            File.WriteAllText(configPath, "{ corrupted json syntax !!! not valid json }");

            var service = new ConfigService(configPath);
            var current = service.Current;

            Assert.NotNull(current);
            Assert.Equal("http://localhost:80", current.BlueRiverUrl);
            Assert.Equal("127.0.0.1", current.ControlServerIp);
        }

        [Fact]
        public void ConfigService_ConfigChanged_EventFiresOnSave()
        {
            string configPath = Path.Combine(_testDirectory, "appsettings.json");
            var service = new ConfigService(configPath);

            AppConfig? receivedConfig = null;
            int eventCount = 0;

            service.ConfigChanged += (cfg) =>
            {
                receivedConfig = cfg;
                eventCount++;
            };

            var newConfig = new AppConfig
            {
                BlueRiverUrl = "http://updated-server:3000",
                ControlServerIp = "10.10.10.10"
            };

            service.Save(newConfig);

            Assert.Equal(1, eventCount);
            Assert.NotNull(receivedConfig);
            Assert.Equal("http://updated-server:3000", receivedConfig.BlueRiverUrl);
            Assert.Equal("10.10.10.10", receivedConfig.ControlServerIp);
        }

        [Fact]
        public void ConfigService_Save_NullThrowsArgumentNullException()
        {
            string configPath = Path.Combine(_testDirectory, "appsettings.json");
            var service = new ConfigService(configPath);

            Assert.Throws<ArgumentNullException>(() => service.Save(null!));
        }

        [Fact]
        public void Validator_DefaultConfig_IsValid()
        {
            var config = new AppConfig();
            var (isValid, errors) = ConfigValidator.Validate(config);

            Assert.True(isValid, $"Default configuration should be valid, but had errors: {string.Join(", ", errors)}");
            Assert.Empty(errors);
        }

        [Theory]
        [InlineData("http://localhost:3000", true)]
        [InlineData("https://192.168.1.10:8443/app", true)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData("not-a-valid-url", false)]
        [InlineData("ftp://localhost:3000", false)]
        public void Validator_BlueRiverUrl_Validation(string url, bool shouldBeValid)
        {
            var config = new AppConfig { BlueRiverUrl = url };
            var (isValid, _) = ConfigValidator.Validate(config);

            Assert.Equal(shouldBeValid, isValid);
        }

        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("192.168.1.100", true)]
        [InlineData("10.0.0.1", true)]
        [InlineData("", false)]
        [InlineData("999.999.999.999", false)]
        [InlineData("abc.def.ghi.jkl", false)]
        [InlineData("192.168.1", false)]
        public void Validator_ControlServerIp_Validation(string ip, bool shouldBeValid)
        {
            var config = new AppConfig { ControlServerIp = ip };
            var (isValid, _) = ConfigValidator.Validate(config);

            Assert.Equal(shouldBeValid, isValid);
        }

        [Theory]
        [InlineData(80, true)]
        [InlineData(8080, true)]
        [InlineData(65535, true)]
        [InlineData(0, false)]
        [InlineData(-1, false)]
        [InlineData(65536, false)]
        public void Validator_PortRanges_Validation(int port, bool shouldBeValid)
        {
            var configRest = new AppConfig { RestPort = port };
            var (isValidRest, _) = ConfigValidator.Validate(configRest);
            Assert.Equal(shouldBeValid, isValidRest);

            var configTelnet = new AppConfig { TelnetPort = port };
            var (isValidTelnet, _) = ConfigValidator.Validate(configTelnet);
            Assert.Equal(shouldBeValid, isValidTelnet);

            var configBase = new AppConfig { BasePort = port };
            var (isValidBase, _) = ConfigValidator.Validate(configBase);
            Assert.Equal(shouldBeValid, isValidBase);
        }

        [Theory]
        [InlineData("224.1.1.1", "224.1.3.225", true)]
        [InlineData("224.0.0.1", "239.255.255.255", true)]
        [InlineData("224.1.1.5", "224.1.1.5", true)] // Single address range
        [InlineData("192.168.1.1", "224.1.1.1", false)] // Start not Class D
        [InlineData("224.1.1.1", "240.0.0.1", false)] // End is Class E
        [InlineData("224.1.3.1", "224.1.1.1", false)] // Start > End
        [InlineData("", "224.1.1.1", false)] // Empty start
        [InlineData("224.1.1.1", "", false)] // Empty end
        public void Validator_MulticastRange_Validation(string startIp, string endIp, bool shouldBeValid)
        {
            var config = new AppConfig
            {
                MulticastStartIp = startIp,
                MulticastEndIp = endIp
            };
            var (isValid, _) = ConfigValidator.Validate(config);

            Assert.Equal(shouldBeValid, isValid);
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("192.168.1.50", true)]
        [InlineData("10.0.0.2", true)]
        [InlineData("invalid-ip", false)]
        [InlineData("999.1.1.1", false)]
        public void Validator_LocalNetworkInterfaceIp_Validation(string localIp, bool shouldBeValid)
        {
            var config = new AppConfig { LocalNetworkInterfaceIp = localIp };
            var (isValid, _) = ConfigValidator.Validate(config);

            Assert.Equal(shouldBeValid, isValid);
        }

        [Theory]
        [InlineData("Dark", "Dark")]
        [InlineData("Light", "Light")]
        [InlineData("dark", "dark")]
        [InlineData("light", "light")]
        [InlineData("Custom", "Dark")] // Fallback
        [InlineData("", "Dark")] // Fallback
        public void Validator_Theme_Normalization(string inputTheme, string expectedTheme)
        {
            var config = new AppConfig { Theme = inputTheme };
            var (isValid, errors) = ConfigValidator.Validate(config);

            Assert.True(isValid);
            Assert.Empty(errors);
            Assert.Equal(expectedTheme, config.Theme, ignoreCase: true);
        }

        [Theory]
        [InlineData(320.0, 320.0)]
        [InlineData(400.0, 400.0)]
        [InlineData(650.0, 650.0)]
        [InlineData(100.0, 400.0)] // Out of bounds fallback
        [InlineData(1000.0, 400.0)] // Out of bounds fallback
        public void Validator_SidebarWidth_Normalization(double inputWidth, double expectedWidth)
        {
            var config = new AppConfig { SidebarWidth = inputWidth };
            var (isValid, errors) = ConfigValidator.Validate(config);

            Assert.True(isValid);
            Assert.Empty(errors);
            Assert.Equal(expectedWidth, config.SidebarWidth);
        }
    }
}
