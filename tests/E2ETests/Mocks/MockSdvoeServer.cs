using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace E2ETests.Mocks
{
    public class MockDevice
    {
        public string MacAddress { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string Model { get; set; } = "AVAS-223";
        public int VendorId { get; set; } = 105;
        public int ProductId { get; set; } = 81;
        public int ChipIndex { get; set; } = 0;
        public bool IsTransmitter { get; set; } = true;
        public bool IsReceiver { get; set; } = false;
        public bool IsStreaming { get; set; } = false;
        public string? MulticastIp { get; set; }
        public int UdpPort { get; set; } = 6792;
        public double Fps { get; set; } = 1.0;
        public uint Ssrc { get; set; } = 12345;
    }

    public class MockSdvoeServer : IDisposable, IAsyncDisposable
    {
        private readonly TcpListener _telnetListener;
        private readonly HttpListener _httpListener;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, MockDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _activeMulticastStreams = new(); // MulticastIP -> MAC
        private readonly List<TcpClient> _connectedClients = new();
        private readonly object _lock = new();

        public int TelnetPort { get; private set; }
        public int RestPort { get; private set; }
        public bool IsRunning { get; private set; }

        public MockSdvoeServer(int telnetPort = 0, int restPort = 0)
        {
            // Setup Telnet listener
            _telnetListener = new TcpListener(IPAddress.Loopback, telnetPort);
            _telnetListener.Start();
            TelnetPort = ((IPEndPoint)_telnetListener.LocalEndpoint).Port;

            // Setup REST listener
            if (restPort == 0)
            {
                using var tempSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tempSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                restPort = ((IPEndPoint)tempSocket.LocalEndPoint!).Port;
            }
            RestPort = restPort;

            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://127.0.0.1:{RestPort}/");
            _httpListener.Start();

            IsRunning = true;

            // Populate default devices as observed in authoritative hardware & control server
            SeedDefaultDevices();

            // Start background accept loops
            Task.Run(AcceptTelnetClientsLoopAsync);
            Task.Run(AcceptHttpRequestsLoopAsync);
        }

        private void SeedDefaultDevices()
        {
            AddDevice(new MockDevice
            {
                MacAddress = "f8228500aaaa",
                DeviceName = "AVAS-223-TX0",
                IpAddress = "192.168.1.151",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = false
            });

            AddDevice(new MockDevice
            {
                MacAddress = "f8228500bbbb",
                DeviceName = "AVAS-223-TX1",
                IpAddress = "192.168.1.150",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 1,
                IsTransmitter = true,
                IsReceiver = false
            });

            AddDevice(new MockDevice
            {
                MacAddress = "f8228500cccc",
                DeviceName = "AVAS-223-RX0",
                IpAddress = "192.168.1.152",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = false,
                IsReceiver = true
            });

            AddDevice(new MockDevice
            {
                MacAddress = "001122334455",
                DeviceName = "ThirdParty-TX",
                IpAddress = "192.168.1.200",
                VendorId = 999,
                ProductId = 50,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = false
            });

            AddDevice(new MockDevice
            {
                MacAddress = "f8228500dddd",
                DeviceName = "AVAS-223-TX0-Unit2",
                IpAddress = "192.168.1.153",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = false
            });
        }

        public void AddDevice(MockDevice device)
        {
            _devices[device.MacAddress] = device;
        }

        public void ClearDevices()
        {
            _devices.Clear();
            _activeMulticastStreams.Clear();
        }

        public MockDevice? GetDevice(string mac)
        {
            _devices.TryGetValue(mac, out var dev);
            return dev;
        }

        public IReadOnlyCollection<MockDevice> GetAllDevices() => new List<MockDevice>(_devices.Values);

        public IReadOnlyDictionary<string, string> GetActiveMulticastStreams() => _activeMulticastStreams;

        private async Task AcceptTelnetClientsLoopAsync()
        {
            while (!_cts.IsCancellationRequested && IsRunning)
            {
                try
                {
                    var client = await _telnetListener.AcceptTcpClientAsync(_cts.Token);
                    lock (_lock)
                    {
                        _connectedClients.Add(client);
                    }
                    _ = Task.Run(() => HandleTelnetClientAsync(client, _cts.Token));
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { break; }
            }
        }

        private async Task HandleTelnetClientAsync(TcpClient client, CancellationToken ct)
        {
            using var netStream = client.GetStream();
            using var reader = new StreamReader(netStream, Encoding.ASCII);
            using var writer = new StreamWriter(netStream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            bool isHandshakeComplete = false;

            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;

                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    // Empirical BlueRiver Behavior: require api 3.0.0.0 MUST be sent first
                    if (!isHandshakeComplete)
                    {
                        if (line.StartsWith("require api 3.", StringComparison.OrdinalIgnoreCase))
                        {
                            isHandshakeComplete = true;
                            await writer.WriteLineAsync("{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":null,\"error\":null}");
                        }
                        else if (line.Equals("version", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync(GetVersionJson());
                        }
                        else
                        {
                            await writer.WriteLineAsync("{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"INVALID_COMMAND\",\"message\":\"Invalid command\"}}");
                        }
                        continue;
                    }

                    // Process authenticated commands
                    string response = ProcessCommand(line);
                    await writer.WriteLineAsync(response);
                }
            }
            catch { }
            finally
            {
                lock (_lock)
                {
                    _connectedClients.Remove(client);
                }
                client.Dispose();
            }
        }

        private string ProcessCommand(string command)
        {
            if (command.Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                return GetVersionJson();
            }

            if (command.Equals("get all identity", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("get all device", StringComparison.OrdinalIgnoreCase))
            {
                return BuildDeviceIdentityListJson();
            }

            if (command.StartsWith("get ", StringComparison.OrdinalIgnoreCase) && command.EndsWith(" identity", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3)
                {
                    string mac = parts[1];
                    if (_devices.TryGetValue(mac, out var device))
                    {
                        return BuildSingleDeviceJson(device);
                    }
                }
                return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{\"devices\":[],\"error\":[]},\"error\":null}";
            }

            if (command.Equals("list multicast", StringComparison.OrdinalIgnoreCase))
            {
                return BuildListMulticastJson();
            }

            // set <mac> thumbnail fps <fps> ssrc <ssrc> [udp <port>]
            if (command.StartsWith("set ", StringComparison.OrdinalIgnoreCase) && command.Contains("thumbnail", StringComparison.OrdinalIgnoreCase))
            {
                return HandleSetThumbnail(command);
            }

            // start <target> <multicast_ip>
            if (command.StartsWith("start ", StringComparison.OrdinalIgnoreCase))
            {
                return HandleStartStream(command);
            }

            // stop <target> [free]
            if (command.StartsWith("stop ", StringComparison.OrdinalIgnoreCase))
            {
                return HandleStopStream(command);
            }

            return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":null,\"error\":null}";
        }

        private string HandleSetThumbnail(string command)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"Invalid argument count\"}}";
            }

            string targetMac = parts[1];

            // Mandatory arguments: ssrc must be present
            if (!command.Contains("ssrc", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"UDP port and SSRC arguments are mandatory\"}}";
            }

            double fps = 1.0;
            uint ssrc = 12345;
            int port = 6792;

            for (int i = 2; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("fps", StringComparison.OrdinalIgnoreCase))
                {
                    double.TryParse(parts[i + 1], out fps);
                }
                else if (parts[i].Equals("ssrc", StringComparison.OrdinalIgnoreCase))
                {
                    uint.TryParse(parts[i + 1], out ssrc);
                }
                else if (parts[i].Equals("udp", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(parts[i + 1], out port);
                }
            }

            if (targetMac.Equals("all_tx", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var dev in _devices.Values)
                {
                    if (dev.IsTransmitter)
                    {
                        dev.Fps = fps;
                        dev.Ssrc = ssrc;
                        dev.UdpPort = port;
                    }
                }
            }
            else if (_devices.TryGetValue(targetMac, out var dev))
            {
                dev.Fps = fps;
                dev.Ssrc = ssrc;
                dev.UdpPort = port;
            }

            return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{\"status\":\"configured\"},\"error\":null}";
        }

        private string HandleStartStream(string command)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"Invalid arguments\"}}";
            }

            string target = parts[1];
            string ip = parts[2];

            // Group start with specific IP is illegal
            if (target.StartsWith("all_tx", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"Command 'start' with a specified IP address can only target a single device\"}}";
            }

            // Must include stream index (e.g. :thumbnail:0)
            if (!target.Contains(":thumbnail:0", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"When specifying an IP address, stream type and index must also be specified\"}}";
            }

            string mac = target.Split(':')[0];
            if (!_devices.TryGetValue(mac, out var device))
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"Invalid device stream argument\"}}";
            }

            // Validate Multicast IP bounds & reserved addresses
            var validationError = ValidateMulticastIp(ip);
            if (validationError != null)
            {
                return validationError;
            }

            device.IsStreaming = true;
            device.MulticastIp = ip;
            _activeMulticastStreams[ip] = mac;

            return $"{{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{{\"stream\":{{\"stream_type\":\"THUMBNAIL\",\"stream_index\":0,\"dest_address\":\"{ip}\",\"dest_port\":{device.UdpPort}}}}},\"error\":null}}";
        }

        private string HandleStopStream(string command)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"Invalid arguments\"}}";
            }

            string target = parts[1];
            bool free = parts.Length >= 3 && parts[2].Equals("free", StringComparison.OrdinalIgnoreCase);

            string mac = target.Split(':')[0];
            if (mac.Equals("all_tx", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var dev in _devices.Values)
                {
                    if (dev.IsTransmitter)
                    {
                        dev.IsStreaming = false;
                        if (free && dev.MulticastIp != null)
                        {
                            _activeMulticastStreams.TryRemove(dev.MulticastIp, out _);
                            dev.MulticastIp = null;
                        }
                    }
                }
            }
            else if (_devices.TryGetValue(mac, out var dev))
            {
                dev.IsStreaming = false;
                if (free && dev.MulticastIp != null)
                {
                    _activeMulticastStreams.TryRemove(dev.MulticastIp, out _);
                    dev.MulticastIp = null;
                }
            }

            return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{\"devices\":[],\"error\":[]},\"error\":null}";
        }

        private string? ValidateMulticastIp(string ipStr)
        {
            if (!IPAddress.TryParse(ipStr, out var ip))
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"Invalid IP address format\"}}";
            }

            if (ipStr.Equals("224.1.1.253", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"Multicast IP address 224.1.1.253 is reserved\"}}";
            }

            if (ipStr.Equals("224.1.1.254", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"Multicast IP address 224.1.1.254 is reserved\"}}";
            }

            byte[] bytes = ip.GetAddressBytes();
            uint ipNum = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

            // 224.1.1.1 = 0xE0010101, 224.1.3.255 = 0xE00103FF
            uint minAllowed = 0xE0010101;
            uint maxAllowed = 0xE00103FF;

            if (ipNum < minAllowed || ipNum > maxAllowed)
            {
                return "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"If a multicast IP address is specified, it must be in the configured range 224.1.1.1 to 224.1.3.255\"}}";
            }

            return null;
        }

        private string GetVersionJson()
        {
            return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{\"server\":\"Control Server\",\"vendor\":\"Semtech\",\"version\":\"3.2.0.1\",\"modules\":[{\"name\":\"api\",\"version\":\"2.32.0.1\"},{\"name\":\"api\",\"version\":\"3.2.0.1\"},{\"name\":\"multiview\",\"version\":\"1.1.0\"}]},\"error\":null}";
        }

        private string BuildDeviceIdentityListJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{\"devices\":[");
            bool first = true;
            foreach (var dev in _devices.Values)
            {
                if (!first) sb.Append(",");
                sb.Append(SerializeDeviceJson(dev));
                first = false;
            }
            sb.Append("],\"error\":[]},\"error\":null}");
            return sb.ToString();
        }

        private string BuildSingleDeviceJson(MockDevice dev)
        {
            return $"{{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{{\"devices\":[{SerializeDeviceJson(dev)}],\"error\":[]}},\"error\":null}}";
        }

        private string SerializeDeviceJson(MockDevice dev)
        {
            return $"{{\"device_id\":\"{dev.MacAddress}\",\"device_name\":\"{dev.DeviceName}\",\"identity\":{{\"chipset_type\":\"AVP2000T\",\"engine\":\"PLETHORA\",\"vendor_id\":{dev.VendorId},\"product_id\":{dev.ProductId},\"firmware_comment\":\"SDVoE v2.2.0.0\",\"firmware_version\":\"1.3.1.0\",\"firmware_rc\":0,\"is_receiver\":{dev.IsReceiver.ToString().ToLowerInvariant()},\"is_transmitter\":{dev.IsTransmitter.ToString().ToLowerInvariant()},\"chip_id\":{dev.ChipIndex}}},\"status\":{{\"active\":true}},\"nodes\":[{{\"type\":\"NETWORK_INTERFACE\",\"index\":0,\"configuration\":{{}},\"status\":{{\"mac_address\":\"{dev.MacAddress}\",\"ip\":{{\"address\":\"{dev.IpAddress}\"}}}}}}]}}";
        }

        private string BuildListMulticastJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{\"multicast\":[");
            bool first = true;
            foreach (var kvp in _activeMulticastStreams)
            {
                if (!first) sb.Append(",");
                sb.Append($"{{\"address\":\"{kvp.Key}\",\"devices\":[\"{kvp.Value}\"],\"stream\":\"THUMBNAIL:0\"}}");
                first = false;
            }
            sb.Append("]},\"error\":null}");
            return sb.ToString();
        }

        private async Task AcceptHttpRequestsLoopAsync()
        {
            while (!_cts.IsCancellationRequested && IsRunning)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    _ = Task.Run(() => HandleHttpRequestAsync(context));
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { break; }
            }
        }

        private async Task HandleHttpRequestAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var resp = context.Response;
            resp.ContentType = "application/json";

            try
            {
                string path = req.Url?.AbsolutePath ?? "/";
                string method = req.HttpMethod;

                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (path.Equals("/api", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonResponseAsync(resp, 200, GetVersionJson());
                        return;
                    }
                    if (path.Equals("/api/device", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonResponseAsync(resp, 200, BuildDeviceIdentityListJson());
                        return;
                    }
                    if (path.Equals("/api/multicast", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonResponseAsync(resp, 200, BuildListMulticastJson());
                        return;
                    }
                    if (path.StartsWith("/api/request/", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonResponseAsync(resp, 200, "{\"status\":\"SUCCESS\",\"result\":{\"completed\":true}}");
                        return;
                    }
                }
                else if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
                    string body = await sr.ReadToEndAsync();

                    if (path.Equals("/api/device/ALL", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/api/device/ALL_TX", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonResponseAsync(resp, 200, BuildDeviceIdentityListJson());
                        return;
                    }

                    // /api/device/{mac}
                    var match = Regex.Match(path, @"^/api/device/([a-fA-F0-9]+)$");
                    if (match.Success)
                    {
                        string mac = match.Groups[1].Value;
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        string op = root.GetProperty("op").GetString() ?? "";

                        if (op.Equals("get", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_devices.TryGetValue(mac, out var dev))
                            {
                                await WriteJsonResponseAsync(resp, 200, BuildSingleDeviceJson(dev));
                            }
                            else
                            {
                                await WriteJsonResponseAsync(resp, 404, "{\"status\":\"ERROR\",\"error\":{\"reason\":\"NOT_FOUND\"}}");
                            }
                            return;
                        }
                        else if (op.Equals("set:thumbnail", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!root.TryGetProperty("ssrc", out _))
                            {
                                await WriteJsonResponseAsync(resp, 400, "{\"status\":\"ERROR\",\"error\":{\"reason\":\"ILLEGAL_ARGUMENT\",\"message\":\"SSRC is mandatory\"}}");
                                return;
                            }
                            await WriteJsonResponseAsync(resp, 200, "{\"status\":\"SUCCESS\",\"result\":null}");
                            return;
                        }
                        else if (op.Equals("start", StringComparison.OrdinalIgnoreCase))
                        {
                            string destAddress = root.GetProperty("dest_address").GetString() ?? "";
                            var err = ValidateMulticastIp(destAddress);
                            if (err != null)
                            {
                                await WriteJsonResponseAsync(resp, 400, err);
                                return;
                            }

                            if (_devices.TryGetValue(mac, out var dev))
                            {
                                dev.IsStreaming = true;
                                dev.MulticastIp = destAddress;
                                _activeMulticastStreams[destAddress] = mac;
                                await WriteJsonResponseAsync(resp, 200, $"{{\"status\":\"SUCCESS\",\"result\":{{\"dest_address\":\"{destAddress}\"}}}}");
                            }
                            else
                            {
                                await WriteJsonResponseAsync(resp, 404, "{\"status\":\"ERROR\",\"error\":{\"reason\":\"NOT_FOUND\"}}");
                            }
                            return;
                        }
                        else if (op.Equals("stop", StringComparison.OrdinalIgnoreCase))
                        {
                            bool free = root.TryGetProperty("free", out var freeElem) && freeElem.GetBoolean();
                            if (_devices.TryGetValue(mac, out var dev))
                            {
                                dev.IsStreaming = false;
                                if (free && dev.MulticastIp != null)
                                {
                                    _activeMulticastStreams.TryRemove(dev.MulticastIp, out _);
                                    dev.MulticastIp = null;
                                }
                                await WriteJsonResponseAsync(resp, 200, "{\"status\":\"SUCCESS\",\"result\":null}");
                            }
                            else
                            {
                                await WriteJsonResponseAsync(resp, 404, "{\"status\":\"ERROR\",\"error\":{\"reason\":\"NOT_FOUND\"}}");
                            }
                            return;
                        }
                    }
                }

                await WriteJsonResponseAsync(resp, 404, "{\"status\":\"ERROR\",\"error\":{\"reason\":\"NOT_FOUND\"}}");
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteJsonResponseAsync(resp, 500, $"{{\"status\":\"ERROR\",\"error\":{{\"reason\":\"INTERNAL_ERROR\",\"message\":\"{ex.Message}\"}}}}");
                }
                catch { }
            }
        }

        private static async Task WriteJsonResponseAsync(HttpListenerResponse resp, int statusCode, string json)
        {
            resp.StatusCode = statusCode;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes);
            resp.OutputStream.Close();
        }

        public void Dispose()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _cts.Cancel();

            try { _telnetListener.Stop(); } catch { }
            try { _httpListener.Stop(); } catch { }

            lock (_lock)
            {
                foreach (var c in _connectedClients)
                {
                    try { c.Dispose(); } catch { }
                }
                _connectedClients.Clear();
            }

            _cts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            await Task.CompletedTask;
        }
    }
}
