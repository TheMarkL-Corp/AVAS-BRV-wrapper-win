using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace E2ETests.Harness
{
    public interface ISdvoeDiscoveryService
    {
        Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(CancellationToken ct = default);
        event Action<IReadOnlyList<AvasDevice>>? DevicesDiscovered;
    }

    public class SdvoeClient : ISdvoeDiscoveryService, IDisposable
    {
        private readonly string _serverIp;
        private readonly int _telnetPort;
        private readonly int _restPort;
        private readonly HttpClient _httpClient;
        private TcpClient? _telnetClient;
        private StreamReader? _telnetReader;
        private StreamWriter? _telnetWriter;
        private readonly SemaphoreSlim _telnetLock = new(1, 1);
        private bool _isTelnetAuthenticated;

        public event Action<IReadOnlyList<AvasDevice>>? DevicesDiscovered;

        public SdvoeClient(string serverIp = "127.0.0.1", int telnetPort = 6970, int restPort = 8080)
        {
            _serverIp = serverIp;
            _telnetPort = telnetPort;
            _restPort = restPort;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        public async Task<bool> ConnectTelnetAsync(CancellationToken ct = default)
        {
            await _telnetLock.WaitAsync(ct);
            try
            {
                if (_telnetClient != null && _telnetClient.Connected && _isTelnetAuthenticated)
                {
                    return true;
                }

                _telnetClient = new TcpClient();
                await _telnetClient.ConnectAsync(_serverIp, _telnetPort, ct);
                var stream = _telnetClient.GetStream();
                _telnetReader = new StreamReader(stream, Encoding.ASCII);
                _telnetWriter = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                // Mandatory Semtech Handshake
                await _telnetWriter.WriteLineAsync("require api 3.0.0.0");
                string? response = await _telnetReader.ReadLineAsync(ct);

                if (response != null && response.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase))
                {
                    _isTelnetAuthenticated = true;
                    return true;
                }

                return false;
            }
            finally
            {
                _telnetLock.Release();
            }
        }

        public async Task<string> SendTelnetCommandAsync(string command, CancellationToken ct = default)
        {
            await _telnetLock.WaitAsync(ct);
            try
            {
                if (_telnetClient == null || !_telnetClient.Connected || _telnetWriter == null || _telnetReader == null)
                {
                    throw new InvalidOperationException("Telnet client is not connected.");
                }

                await _telnetWriter.WriteLineAsync(command);
                string? line = await _telnetReader.ReadLineAsync(ct);
                return line ?? string.Empty;
            }
            finally
            {
                _telnetLock.Release();
            }
        }

        public async Task<IReadOnlyList<AvasDevice>> DiscoverDevicesViaTelnetAsync(CancellationToken ct = default)
        {
            if (!_isTelnetAuthenticated)
            {
                bool connected = await ConnectTelnetAsync(ct);
                if (!connected) return Array.Empty<AvasDevice>();
            }

            string response = await SendTelnetCommandAsync("get all identity", ct);
            return ParseDevicesJson(response);
        }

        public async Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(CancellationToken ct = default)
        {
            var all = await DiscoverDevicesViaTelnetAsync(ct);
            var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(all);
            DevicesDiscovered?.Invoke(filtered);
            return filtered;
        }

        public async Task<IReadOnlyList<AvasDevice>> DiscoverDevicesViaRestAsync(CancellationToken ct = default)
        {
            string url = $"http://{_serverIp}:{_restPort}/api/device/ALL";
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{\"op\":\"get\",\"subset\":\"identity\"}", Encoding.UTF8, "application/json")
            };

            var resp = await _httpClient.SendAsync(request, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            return ParseDevicesJson(json);
        }

        public async Task<bool> ConfigureThumbnailStreamAsync(string mac, double fps, uint ssrc, int udpPort, CancellationToken ct = default)
        {
            if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
            string cmd = $"set {mac} thumbnail fps {fps:0.0} ssrc {ssrc} udp {udpPort}";
            string resp = await SendTelnetCommandAsync(cmd, ct);
            return resp.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<bool> StartPreviewStreamAsync(string mac, string multicastIp, CancellationToken ct = default)
        {
            if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
            string cmd = $"start {mac}:thumbnail:0 {multicastIp}";
            string resp = await SendTelnetCommandAsync(cmd, ct);
            return resp.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<bool> StopPreviewStreamAsync(string mac, bool free = true, CancellationToken ct = default)
        {
            if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
            string cmd = free ? $"stop {mac}:thumbnail:0 free" : $"stop {mac}:thumbnail:0";
            string resp = await SendTelnetCommandAsync(cmd, ct);
            return resp.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase);
        }

        public static IReadOnlyList<AvasDevice> ParseDevicesJson(string json)
        {
            var list = new List<AvasDevice>();
            if (string.IsNullOrWhiteSpace(json)) return list;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out var resultElem)) return list;
                if (!resultElem.TryGetProperty("devices", out var devicesElem)) return list;

                foreach (var d in devicesElem.EnumerateArray())
                {
                    string mac = d.TryGetProperty("device_id", out var macProp) ? macProp.GetString() ?? "" : "";
                    string name = d.TryGetProperty("device_name", out var nameProp) ? nameProp.GetString() ?? "" : "";

                    int vid = 0, pid = 0, chip = 0;
                    bool isTx = false, isRx = false;

                    if (d.TryGetProperty("identity", out var idElem))
                    {
                        if (idElem.TryGetProperty("vendor_id", out var vidProp)) vid = vidProp.GetInt32();
                        if (idElem.TryGetProperty("product_id", out var pidProp)) pid = pidProp.GetInt32();
                        if (idElem.TryGetProperty("chip_id", out var chipProp)) chip = chipProp.GetInt32();
                        if (idElem.TryGetProperty("is_transmitter", out var txProp)) isTx = txProp.GetBoolean();
                        if (idElem.TryGetProperty("is_receiver", out var rxProp)) isRx = rxProp.GetBoolean();
                    }

                    string ip = "";
                    if (d.TryGetProperty("nodes", out var nodesElem))
                    {
                        foreach (var node in nodesElem.EnumerateArray())
                        {
                            if (node.TryGetProperty("status", out var nodeStatus) &&
                                nodeStatus.TryGetProperty("ip", out var ipObj) &&
                                ipObj.TryGetProperty("address", out var ipAddr))
                            {
                                ip = ipAddr.GetString() ?? "";
                                break;
                            }
                        }
                    }

                    list.Add(new AvasDevice
                    {
                        MacAddress = mac,
                        DeviceName = name,
                        IpAddress = ip,
                        VendorId = vid,
                        ProductId = pid,
                        ChipIndex = chip,
                        IsTransmitter = isTx,
                        IsReceiver = isRx
                    });
                }
            }
            catch { }

            return list;
        }

        public void DisconnectTelnet()
        {
            _telnetLock.Wait();
            try
            {
                _isTelnetAuthenticated = false;
                _telnetReader?.Dispose();
                _telnetWriter?.Dispose();
                _telnetClient?.Dispose();
                _telnetReader = null;
                _telnetWriter = null;
                _telnetClient = null;
            }
            finally
            {
                _telnetLock.Release();
            }
        }

        public void Dispose()
        {
            DisconnectTelnet();
            _httpClient.Dispose();
            _telnetLock.Dispose();
        }
    }
}
