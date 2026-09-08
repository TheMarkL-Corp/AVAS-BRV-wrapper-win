using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace E2ETests.Harness
{
    public interface IRtpStreamReceiver : IDisposable
    {
        void StartListening(string multicastIp, int port, string localInterfaceIp = "");
        void StopListening();
        void ProcessDatagram(ReadOnlySpan<byte> datagram);
        event Action<byte[], int, int>? FrameReady;
        event Action<double>? FpsUpdated;
    }

    public class RtpMulticastReceiver : IRtpStreamReceiver
    {
        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private readonly ScanlineFrameReassembler _reassembler = new();
        private readonly Queue<long> _frameTimestamps = new();
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _fpsLock = new();
        private bool _isListening;

        public event Action<byte[], int, int>? FrameReady;
        public event Action<double>? FpsUpdated;

        public double CurrentFps { get; private set; }
        public int ReceivedPacketsCount { get; private set; }
        public int ReceivedFramesCount { get; private set; }
        public int Port { get; private set; }

        public void StartListening(string multicastIp, int port, string localInterfaceIp = "")
        {
            if (_isListening) StopListening();

            Port = port;
            _cts = new CancellationTokenSource();
            _isListening = true;

            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            var localEp = new IPEndPoint(IPAddress.Any, port);
            _udpClient.Client.Bind(localEp);

            if (_udpClient.Client.LocalEndPoint is IPEndPoint boundEp)
            {
                Port = boundEp.Port;
            }

            if (IPAddress.TryParse(multicastIp, out var mcastAddr) &&
                mcastAddr.GetAddressBytes()[0] >= 224 && mcastAddr.GetAddressBytes()[0] <= 239)
            {
                if (!string.IsNullOrEmpty(localInterfaceIp) && IPAddress.TryParse(localInterfaceIp, out var localNic))
                {
                    _udpClient.JoinMulticastGroup(mcastAddr, localNic);
                }
                else
                {
                    _udpClient.JoinMulticastGroup(mcastAddr);
                }
            }

            Task.Run(() => ReceiveLoopAsync(_udpClient, _cts.Token));
        }

        public void ProcessDatagram(ReadOnlySpan<byte> datagram)
        {
            ReceivedPacketsCount++;
            if (RtpPacketParser.TryParse(datagram, out var rtpHeader))
            {
                var frame = _reassembler.ProcessPacket(rtpHeader);
                if (frame != null)
                {
                    ReceivedFramesCount++;
                    UpdateFps();

                    byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(frame.YuvData, frame.Width, frame.Height);
                    FrameReady?.Invoke(rgb, frame.Width, frame.Height);
                }
            }
        }

        private async Task ReceiveLoopAsync(UdpClient client, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isListening)
            {
                try
                {
                    var result = await client.ReceiveAsync(ct);
                    ProcessDatagram(result.Buffer);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { break; }
            }
        }

        private void UpdateFps()
        {
            lock (_fpsLock)
            {
                long now = _stopwatch.ElapsedMilliseconds;
                _frameTimestamps.Enqueue(now);

                // Discard timestamps older than 1.5 seconds
                while (_frameTimestamps.Count > 0 && (now - _frameTimestamps.Peek()) > 1500)
                {
                    _frameTimestamps.Dequeue();
                }

                if (_frameTimestamps.Count >= 2)
                {
                    double durationSec = (now - _frameTimestamps.Peek()) / 1000.0;
                    if (durationSec > 0.05)
                    {
                        CurrentFps = Math.Round((_frameTimestamps.Count - 1) / durationSec, 1);
                        FpsUpdated?.Invoke(CurrentFps);
                    }
                }
                else
                {
                    CurrentFps = 1.0;
                    FpsUpdated?.Invoke(CurrentFps);
                }
            }
        }

        public void StopListening()
        {
            if (!_isListening) return;
            _isListening = false;
            _cts?.Cancel();
            try { _udpClient?.Close(); } catch { }
            try { _udpClient?.Dispose(); } catch { }
            _udpClient = null;
            _cts?.Dispose();
            _cts = null;
            _reassembler.Reset();
        }

        public void Dispose()
        {
            StopListening();
        }
    }
}
