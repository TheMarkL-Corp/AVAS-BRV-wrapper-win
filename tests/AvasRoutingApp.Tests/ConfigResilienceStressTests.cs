using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvasRoutingApp.Configuration;

namespace AvasRoutingApp.Tests
{
    public class ConfigResilienceStressTests : IDisposable
    {
        private readonly string _testDir;

        public ConfigResilienceStressTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "AvasChallengerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDir))
                {
                    Directory.Delete(_testDir, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        #region Category 1: Corrupted & Edge-Case Files

        [Fact]
        public void TruncatedJson_FallsBackToDefault_WithoutCrashing()
        {
            string path = Path.Combine(_testDir, "truncated.json");
            File.WriteAllText(path, "{\"BlueRiverUrl\": \"http://localhost:3000\", \"ControlServerIp\": \"192.168.");

            var service = new ConfigService(path);
            var current = service.Current;

            Assert.NotNull(current);
            Assert.Equal("http://localhost:80", current.BlueRiverUrl);
            Assert.Equal("127.0.0.1", current.ControlServerIp);
            Assert.Equal(8090, current.RestPort);
        }

        [Fact]
        public void ZeroByteEmptyFile_FallsBackToDefault_WithoutCrashing()
        {
            string path = Path.Combine(_testDir, "empty.json");
            File.WriteAllBytes(path, Array.Empty<byte>());

            var service = new ConfigService(path);
            var current = service.Current;

            Assert.NotNull(current);
            Assert.Equal("http://localhost:80", current.BlueRiverUrl);
            Assert.Equal(8090, current.RestPort);
        }

        [Fact]
        public void WhitespaceOnlyFile_FallsBackToDefault_WithoutCrashing()
        {
            string path = Path.Combine(_testDir, "whitespace.json");
            File.WriteAllText(path, "   \r\n\t  \n   ");

            var service = new ConfigService(path);
            var current = service.Current;

            Assert.NotNull(current);
            Assert.Equal("http://localhost:80", current.BlueRiverUrl);
        }

        [Fact]
        public void NonUtf8BinaryJunk_FallsBackToDefault_WithoutCrashing()
        {
            string path = Path.Combine(_testDir, "binary_junk.json");
            byte[] junkBytes = new byte[] { 0xFF, 0xFE, 0x00, 0xBA, 0xBE, 0xEF, 0x12, 0x34, 0x56, 0x78 };
            File.WriteAllBytes(path, junkBytes);

            var service = new ConfigService(path);
            var current = service.Current;

            Assert.NotNull(current);
            Assert.Equal("http://localhost:80", current.BlueRiverUrl);
            Assert.Equal("127.0.0.1", current.ControlServerIp);
        }

        [Fact]
        public void JsonLiteralNull_FallsBackToDefault_WithoutCrashing()
        {
            string path = Path.Combine(_testDir, "literal_null.json");
            File.WriteAllText(path, "null");

            var service = new ConfigService(path);
            var current = service.Current;

            Assert.NotNull(current);
            Assert.Equal("http://localhost:80", current.BlueRiverUrl);
            Assert.Equal(8090, current.RestPort);
        }

        [Fact]
        public void MissingFieldsInJson_RetainsDefaultsForMissing()
        {
            string path = Path.Combine(_testDir, "partial.json");
            // Only TelnetPort is specified
            File.WriteAllText(path, "{\"TelnetPort\": 7777}");

            var service = new ConfigService(path);
            var current = service.Current;

            Assert.NotNull(current);
            Assert.Equal(7777, current.TelnetPort);
            // Missing fields must have their defaults
            Assert.Equal("http://localhost:80", current.BlueRiverUrl);
            Assert.Equal("127.0.0.1", current.ControlServerIp);
            Assert.Equal(8090, current.RestPort);
            Assert.Equal("224.1.3.1", current.MulticastStartIp);
            Assert.Equal("224.1.3.225", current.MulticastEndIp);
            Assert.Equal(5000, current.BasePort);
        }

        [Fact]
        public void ExtraUnknownPropertiesInJson_IgnoredCleanly()
        {
            string path = Path.Combine(_testDir, "extra_properties.json");
            File.WriteAllText(path, "{\"FutureField\": true, \"NestedObject\": {\"key\": 123}, \"BlueRiverUrl\": \"http://future-box:3000\"}");

            var service = new ConfigService(path);
            var current = service.Current;

            Assert.NotNull(current);
            Assert.Equal("http://future-box:3000", current.BlueRiverUrl);
            Assert.Equal(8090, current.RestPort);
        }

        [Fact]
        public void DeeplyNestedDirectory_CreatedAutomaticallyOnSave()
        {
            string nestedPath = Path.Combine(_testDir, "level1", "level2", "level3", "appsettings.json");
            var service = new ConfigService(nestedPath);

            var config = new AppConfig { ControlServerIp = "10.20.30.40" };
            service.Save(config);

            Assert.True(File.Exists(nestedPath));
            var reloaded = new ConfigService(nestedPath);
            Assert.Equal("10.20.30.40", reloaded.Current.ControlServerIp);
        }

        [Fact]
        public void LockedFile_SaveThrowsException_PreservesCurrentInMemoryState()
        {
            string path = Path.Combine(_testDir, "locked_config.json");
            var service = new ConfigService(path);

            var validOriginal = service.Current;

            // Lock the file exclusively
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var newConfig = new AppConfig { ControlServerIp = "192.168.99.99" };

                // Save should fail because destination is locked on Windows (throws UnauthorizedAccessException or IOException)
                var ex = Assert.ThrowsAny<Exception>(() => service.Save(newConfig));
                Assert.True(ex is IOException || ex is UnauthorizedAccessException,
                    $"Expected IOException or UnauthorizedAccessException, but got {ex.GetType().Name}");

                // Verify in-memory state is preserved, not corrupted
                Assert.Equal(validOriginal.ControlServerIp, service.Current.ControlServerIp);
            }
        }

        [Fact]
        public void ReadOnlyFile_SaveFailsAndPreservesInMemoryState()
        {
            string path = Path.Combine(_testDir, "readonly_config.json");
            var service = new ConfigService(path);
            var original = service.Current;

            // Mark the destination file as ReadOnly
            File.SetAttributes(path, FileAttributes.ReadOnly);

            try
            {
                var newConfig = new AppConfig { ControlServerIp = "10.99.99.99" };
                var ex = Assert.ThrowsAny<Exception>(() => service.Save(newConfig));
                Assert.True(ex is UnauthorizedAccessException || ex is IOException);

                // Ensure in-memory state remains intact
                Assert.Equal(original.ControlServerIp, service.Current.ControlServerIp);
            }
            finally
            {
                // Remove ReadOnly attribute for test cleanup
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }

        [Fact]
        public void SaveFailure_WhenLocked_PreservesFileIntegrityOnDisk()
        {
            string path = Path.Combine(_testDir, "temp_leak_test.json");
            var service = new ConfigService(path);
            var initialConfig = service.Current;

            // Ensure destination file exists and is locked
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var newConfig = new AppConfig { ControlServerIp = "192.168.99.99" };
                try
                {
                    service.Save(newConfig);
                }
                catch (Exception)
                {
                    // Expected to fail because file is locked
                }
            }

            // Verify original file on disk was not corrupted or truncated
            string content = File.ReadAllText(path);
            var onDisk = JsonSerializer.Deserialize<AppConfig>(content);
            Assert.NotNull(onDisk);
            Assert.Equal(initialConfig.ControlServerIp, onDisk.ControlServerIp);
            Assert.Equal(initialConfig.ControlServerIp, service.Current.ControlServerIp);
        }




        #endregion

        #region Category 2: IP Address and Port Validation Boundaries

        [Theory]
        [InlineData("0.0.0.0", true)]
        [InlineData("255.255.255.255", true)]
        [InlineData("1.1.1.1", true)]
        [InlineData("127.0.0.1", true)]
        [InlineData("192.168.1.1", true)]
        [InlineData("256.0.0.1", false)]
        [InlineData("192.168.1.-1", false)]
        [InlineData("192.168.1.1.1", false)]
        [InlineData("192.168.1", false)]
        [InlineData("192.168.1.", false)]
        [InlineData(".192.168.1.1", false)]
        [InlineData("192.168.1. 1", false)]
        [InlineData("192.168.1.+1", false)]
        [InlineData("fe80::1", false)]
        [InlineData("::1", false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void ConfigValidator_IPv4BoundaryConditions(string? ip, bool expectedValid)
        {
            bool actual = ConfigValidator.IsValidIPv4Address(ip!);
            Assert.Equal(expectedValid, actual);
        }

        [Theory]
        // Boundary: Class D strictly spans 224.0.0.0 to 239.255.255.255
        [InlineData("224.0.0.0", true)]
        [InlineData("224.0.0.1", true)]
        [InlineData("224.1.1.1", true)]
        [InlineData("224.1.3.225", true)]
        [InlineData("239.255.255.254", true)]
        [InlineData("239.255.255.255", true)]
        // Just outside Class D
        [InlineData("223.255.255.255", false)] // Class C upper bound
        [InlineData("240.0.0.0", false)]       // Class E lower bound
        [InlineData("0.0.0.0", false)]
        [InlineData("127.0.0.1", false)]
        [InlineData("192.168.1.1", false)]
        [InlineData("255.255.255.255", false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void ConfigValidator_MulticastIpBoundaries(string? ip, bool expectedValid)
        {
            bool actual = ConfigValidator.IsValidMulticastIp(ip!, out _);
            Assert.Equal(expectedValid, actual);
        }

        [Theory]
        // Identical start and end: valid single IP pool
        [InlineData("224.1.1.1", "224.1.1.1", true)]
        [InlineData("224.0.0.0", "239.255.255.255", true)]
        [InlineData("224.1.1.1", "224.1.3.225", true)]
        // Inverted: Start > End in 4th octet
        [InlineData("224.1.1.2", "224.1.1.1", false)]
        // Inverted: Start > End in 3rd octet
        [InlineData("224.1.4.1", "224.1.3.225", false)]
        // Inverted: Start > End in 2nd octet
        [InlineData("224.2.1.1", "224.1.3.225", false)]
        // Inverted: Start > End in 1st octet
        [InlineData("225.1.1.1", "224.1.1.1", false)]
        // Octet boundary rollovers
        [InlineData("224.255.255.255", "225.0.0.0", true)]
        [InlineData("225.0.0.0", "224.255.255.255", false)]
        [InlineData("224.0.255.255", "224.1.0.0", true)]
        [InlineData("224.1.0.0", "224.0.255.255", false)]
        // Non-multicast start or end
        [InlineData("223.255.255.255", "224.1.1.1", false)]
        [InlineData("224.1.1.1", "240.0.0.0", false)]
        public void ConfigValidator_MulticastRangeBoundaries(string startIp, string endIp, bool expectedValid)
        {
            var config = new AppConfig
            {
                MulticastStartIp = startIp,
                MulticastEndIp = endIp
            };
            var (isValid, _) = ConfigValidator.Validate(config);
            Assert.Equal(expectedValid, isValid);
        }

        [Theory]
        [InlineData(1, true)]
        [InlineData(65535, true)]
        [InlineData(80, true)]
        [InlineData(8080, true)]
        [InlineData(6970, true)]
        [InlineData(6792, true)]
        [InlineData(0, false)]
        [InlineData(-1, false)]
        [InlineData(65536, false)]
        [InlineData(-65535, false)]
        [InlineData(int.MinValue, false)]
        [InlineData(int.MaxValue, false)]
        public void ConfigValidator_PortBoundaries(int port, bool expectedValid)
        {
            Assert.Equal(expectedValid, ConfigValidator.IsValidPort(port));

            var configRest = new AppConfig { RestPort = port };
            Assert.Equal(expectedValid, ConfigValidator.Validate(configRest).IsValid);

            var configTelnet = new AppConfig { TelnetPort = port };
            Assert.Equal(expectedValid, ConfigValidator.Validate(configTelnet).IsValid);

            var configBase = new AppConfig { BasePort = port };
            Assert.Equal(expectedValid, ConfigValidator.Validate(configBase).IsValid);
        }

        [Theory]
        [InlineData("http://localhost:3000", true)]
        [InlineData("https://localhost:3000", true)]
        [InlineData("http://127.0.0.1:8080", true)]
        [InlineData("http://blueriver.local:3000/app", true)]
        [InlineData("http://blueriver.local:3000/app?token=abc#section", true)]
        [InlineData("ftp://localhost:3000", false)]
        [InlineData("ws://localhost:3000", false)]
        [InlineData("file:///C:/appsettings.json", false)]
        [InlineData("javascript:alert(1)", false)]
        [InlineData("http://", false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void ConfigValidator_BlueRiverUrlBoundaries(string? url, bool expectedValid)
        {
            var config = new AppConfig { BlueRiverUrl = url! };
            var (isValid, _) = ConfigValidator.Validate(config);
            Assert.Equal(expectedValid, isValid);
        }

        [Fact]
        public void ConfigValidator_AllFieldsNull_DoesNotThrowNullReferenceException()
        {
            var config = new AppConfig
            {
                BlueRiverUrl = null!,
                ControlServerIp = null!,
                MulticastStartIp = null!,
                MulticastEndIp = null!,
                LocalNetworkInterfaceIp = null!
            };

            var ex = Record.Exception(() =>
            {
                var (isValid, errors) = ConfigValidator.Validate(config);
                Assert.False(isValid);
                Assert.NotEmpty(errors);
            });

            Assert.Null(ex);
        }

        #endregion

        #region Category 3: Concurrency & Stress Harness

        [Fact]
        public async Task ConcurrentSaves_SingleServiceInstance_NoCorruptionOrCrash()
        {
            string path = Path.Combine(_testDir, "concurrent_single.json");
            var service = new ConfigService(path);

            const int taskCount = 20;
            const int iterationsPerTask = 50;
            var exceptions = new ConcurrentBag<Exception>();

            var tasks = Enumerable.Range(0, taskCount).Select(taskId => Task.Run(() =>
            {
                for (int i = 0; i < iterationsPerTask; i++)
                {
                    try
                    {
                        var config = new AppConfig
                        {
                            BlueRiverUrl = $"http://task-{taskId}-{i}.local:3000",
                            ControlServerIp = "192.168.1." + ((taskId * 10 + i) % 250 + 1),
                            RestPort = 8000 + taskId,
                            TelnetPort = 6000 + taskId,
                            BasePort = 5000 + taskId
                        };

                        service.Save(config);

                        // Read back Current
                        var current = service.Current;
                        Assert.NotNull(current);
                        Assert.StartsWith("http://task-", current.BlueRiverUrl);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            })).ToArray();

            await Task.WhenAll(tasks);

            Assert.Empty(exceptions);

            // Final file should be valid JSON on disk
            Assert.True(File.Exists(path));
            string json = File.ReadAllText(path);
            var finalLoaded = JsonSerializer.Deserialize<AppConfig>(json);
            Assert.NotNull(finalLoaded);
            Assert.StartsWith("http://task-", finalLoaded.BlueRiverUrl);

            // Verify no temp files leaked
            var tempFiles = Directory.GetFiles(_testDir, "*tmp*");
            Assert.Empty(tempFiles);
        }

        [Fact]
        public async Task ConcurrentSavesAndReads_HighVolume_NoDeadlockOrNullReturn()
        {
            string path = Path.Combine(_testDir, "concurrent_rw.json");
            var service = new ConfigService(path);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var exceptions = new ConcurrentBag<Exception>();
            int readCount = 0;
            int writeCount = 0;

            // 5 writers
            var writers = Enumerable.Range(0, 5).Select(id => Task.Run(() =>
            {
                int counter = 0;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        service.Save(new AppConfig
                        {
                            BlueRiverUrl = $"http://writer-{id}-{counter++}.test:3000",
                            RestPort = 8000 + (counter % 1000)
                        });
                        Interlocked.Increment(ref writeCount);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));

            // 10 readers
            var readers = Enumerable.Range(0, 10).Select(id => Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var cur = service.Current;
                        Assert.NotNull(cur);
                        Assert.NotNull(cur.BlueRiverUrl);
                        Assert.True(cur.RestPort >= 8000);
                        Interlocked.Increment(ref readCount);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));

            await Task.WhenAll(writers.Concat(readers));

            Assert.Empty(exceptions);
            Assert.True(writeCount > 50, $"Expected >50 writes, got {writeCount}");
            Assert.True(readCount > 500, $"Expected >500 reads, got {readCount}");

            // Verify no temp files leaked
            var tempFiles = Directory.GetFiles(_testDir, "*tmp*");
            Assert.Empty(tempFiles);
        }

        [Fact]
        public async Task MultipleServiceInstances_CompetingOnSameFile_MaintainsIntegrity()
        {
            string path = Path.Combine(_testDir, "multi_instance.json");
            var serviceInitial = new ConfigService(path);

            const int instanceCount = 6;
            var services = Enumerable.Range(0, instanceCount)
                .Select(_ => new ConfigService(path))
                .ToList();

            var exceptions = new ConcurrentBag<Exception>();
            int successfulSaves = 0;

            var tasks = services.Select((svc, idx) => Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        svc.Save(new AppConfig
                        {
                            BlueRiverUrl = $"http://inst-{idx}-iter-{i}:3000",
                            RestPort = 8000 + idx
                        });
                        Interlocked.Increment(ref successfulSaves);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // On Windows, simultaneous MoveFileEx from two separate processes/instances
                        // might encounter a sharing violation on the destination file.
                        // This is expected OS file contention between independent handles.
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            })).ToArray();

            await Task.WhenAll(tasks);

            Assert.Empty(exceptions);
            Assert.True(successfulSaves > 0, "At least some saves should succeed.");

            // Verify the file on disk remains valid JSON and not corrupt
            string diskContent = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<AppConfig>(diskContent);
            Assert.NotNull(parsed);
            Assert.StartsWith("http://inst-", parsed.BlueRiverUrl);
        }

        #endregion
    }
}
