using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace E2ETests.Harness
{
    public class MockTelnetServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<string> _receivedCommands = new();
        private readonly object _lock = new();
        private readonly bool _handshakeSuccess;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }

        public IReadOnlyList<string> ReceivedCommands
        {
            get
            {
                lock (_lock) return new List<string>(_receivedCommands);
            }
        }

        public MockTelnetServer(bool handshakeSuccess = true)
        {
            _handshakeSuccess = handshakeSuccess;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Start()
        {
            Task.Run(AcceptLoopAsync);
            return Port;
        }

        public void Stop() => Dispose();

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleClientAsync(tcpClient, _cts.Token));
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII))
            using (var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" })
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(ct);
                    }
                    catch
                    {
                        break;
                    }

                    if (line == null) break;

                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    lock (_lock)
                    {
                        _receivedCommands.Add(trimmed);
                    }

                    string response = HandleCommand(trimmed);
                    try
                    {
                        await writer.WriteLineAsync(response);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        private string HandleCommand(string command)
        {
            if (command.StartsWith("require api", StringComparison.OrdinalIgnoreCase))
            {
                return _handshakeSuccess
                    ? "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":null,\"error\":null}"
                    : "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"INVALID_COMMAND\",\"message\":\"Invalid\"}}";
            }

            if (command.Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"status\":\"SUCCESS\",\"result\":{\"server\":\"Control Server\",\"version\":\"3.2.0.1\"},\"error\":null}";
            }

            if (command.Equals("get all identity", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{\"devices\":[{\"device_id\":\"74fe4887fdf4\",\"device_name\":\"AVAS-223-TX0\",\"identity\":{\"vendor_id\":105,\"product_id\":81,\"chip_id\":0,\"is_transmitter\":true,\"is_receiver\":false},\"nodes\":[{\"type\":\"NETWORK_INTERFACE\",\"status\":{\"ip\":{\"address\":\"192.168.1.151\"}}}]},{\"device_id\":\"74fe4887fdf5\",\"device_name\":\"AVAS-223-TX1\",\"identity\":{\"vendor_id\":105,\"product_id\":81,\"chip_id\":1,\"is_transmitter\":true,\"is_receiver\":false},\"nodes\":[{\"type\":\"NETWORK_INTERFACE\",\"status\":{\"ip\":{\"address\":\"192.168.1.150\"}}}]}],\"error\":[]},\"error\":null}";
            }

            if (command.StartsWith("set ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("start ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("stop ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("send ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("list multicast", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{\"status\":\"ok\"},\"error\":null}";
            }

            return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":null,\"error\":null}";
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                _listener.Stop();
                _cts.Dispose();
            }
            catch { }
        }
    }
}
