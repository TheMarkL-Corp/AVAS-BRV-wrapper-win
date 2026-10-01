using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvasRoutingApp.Logging;

namespace AvasRoutingApp.Rtp
{
    /// <summary>
    /// Singleton UDP multicast receiver and demultiplexer operating on port 5000.
    /// Eliminates Windows Winsock port-collision issues by hosting a single high-performance
    /// socket with an 8MB buffer and demultiplexing incoming RTP packets by the transmitter's
    /// source unicast IP address, mirroring the proven architecture of Adv_VOIPS_Sample.
    /// </summary>
    public class UnifiedRtpDemuxReceiver : IDisposable
    {
        private static readonly Lazy<UnifiedRtpDemuxReceiver> _lazyInstance =
            new(() => new UnifiedRtpDemuxReceiver());

        public static UnifiedRtpDemuxReceiver Instance => _lazyInstance.Value;

        private readonly ConcurrentDictionary<string, DemuxChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _joinedMulticastGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _socketLock = new();

        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;
        private int _port = 5000;
        private bool _isListening;
        private bool _disposed;

        public int Port => _port;
        public bool IsListening => _isListening;
        public int ChannelCount => _channels.Count;

        public UnifiedRtpDemuxReceiver(int port = 5000)
        {
            _port = port;
        }

        /// <summary>
        /// Gets or creates a channel receiver associated with an encoder's unicast IP and multicast group.
        /// The returned channel implements IRtpStreamReceiver and can be attached directly to an EncoderCardViewModel.
        /// </summary>
        public IRtpStreamReceiver GetOrCreateChannel(string unicastIp, string multicastIp = "")
        {
            if (string.IsNullOrWhiteSpace(unicastIp))
            {
                unicastIp = "0.0.0.0";
            }

            var channel = _channels.GetOrAdd(unicastIp, ip => new DemuxChannel(this, ip, multicastIp));
            if (!string.IsNullOrWhiteSpace(multicastIp))
            {
                channel.MulticastIp = multicastIp;
                EnsureMulticastGroupJoined(multicastIp);
            }
            return channel;
        }

        /// <summary>
        /// Removes and disposes an existing channel for the specified unicast IP.
        /// </summary>
        public void RemoveChannel(string unicastIp)
        {
            if (string.IsNullOrWhiteSpace(unicastIp)) return;
            if (_channels.TryRemove(unicastIp, out var channel))
            {
                channel.Dispose();
            }

            lock (_socketLock)
            {
                if (_channels.IsEmpty && _isListening)
                {
                    StopListeningInternal();
                }
            }
        }

        /// <summary>
        /// Starts the unified UDP listener on port 5000 if not already active.
        /// </summary>
        public void EnsureStarted(int port = 5000)
        {
            lock (_socketLock)
            {
                if (_isListening) return;

                _port = port;
                _cts = new CancellationTokenSource();
                _isListening = true;

                try
                {
                    _udpClient = new UdpClient();
                    _udpClient.ExclusiveAddressUse = false;
                    _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _udpClient.Client.ReceiveBufferSize = 8 * 1024 * 1024; // 8MB buffer

                    var localEp = new IPEndPoint(IPAddress.Any, _port);
                    _udpClient.Client.Bind(localEp);

                    // Re-join any previously tracked multicast groups
                    foreach (var mcastIp in _joinedMulticastGroups)
                    {
                        JoinMulticastGroupInternal(mcastIp);
                    }

                    var token = _cts.Token;
                    _receiveTask = Task.Run(() => ReceiveLoopAsync(_udpClient, token), token);
                    AppLogger.Info("UnifiedDemux", $"Unified RTP Demux receiver active on port {_port}");
                }
                catch (Exception ex)
                {
                    AppLogger.Error("UnifiedDemux", $"Failed to bind unified RTP socket on port {_port}", ex);
                    StopListeningInternal();
                    throw;
                }
            }
        }

        /// <summary>
        /// Joins the specified multicast group across all available local IPv4 network interfaces.
        /// </summary>
        public void EnsureMulticastGroupJoined(string multicastIp)
        {
            if (string.IsNullOrWhiteSpace(multicastIp)) return;
            lock (_socketLock)
            {
                if (!_joinedMulticastGroups.Contains(multicastIp))
                {
                    _joinedMulticastGroups.Add(multicastIp);
                    if (_isListening && _udpClient != null)
                    {
                        JoinMulticastGroupInternal(multicastIp);
                    }
                }
            }
        }

        private void JoinMulticastGroupInternal(string multicastIp)
        {
            if (_udpClient == null) return;
            if (!IPAddress.TryParse(multicastIp, out var mcastAddr)) return;
            byte firstByte = mcastAddr.GetAddressBytes()[0];
            if (firstByte < 224 || firstByte > 239) return;

            bool joinedAny = false;
            try
            {
                var hostAddrs = Dns.GetHostAddresses(Dns.GetHostName());
                foreach (var addr in hostAddrs)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        try
                        {
                            _udpClient.JoinMulticastGroup(mcastAddr, addr);
                            joinedAny = true;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (!joinedAny)
            {
                try
                {
                    _udpClient.JoinMulticastGroup(mcastAddr);
                }
                catch (SocketException)
                {
                    // Allow unjoined listen in loopback or virtualized environments
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
                    if (result.Buffer != null && result.Buffer.Length >= RtpHeader.MinimumHeaderSize)
                    {
                        string senderIp = result.RemoteEndPoint.Address.ToString();
                        DemuxPacket(senderIp, result.Buffer);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    AppLogger.Warn("UnifiedDemux", $"Receive loop error: {ex.Message}");
                    break;
                }
            }
        }

        /// <summary>
        /// Demultiplexes a datagram to the appropriate channel based on sender IP address.
        /// </summary>
        public void DemuxPacket(string senderIp, ReadOnlySpan<byte> datagram)
        {
            if (_channels.TryGetValue(senderIp, out var channel))
            {
                channel.ProcessDatagram(datagram);
                return;
            }

            // Fallback: If only one active channel is registered, route packet to it
            // (common in 1-TX test topologies where unicast IP is being bound or negotiated)
            if (_channels.Count == 1)
            {
                foreach (var kvp in _channels)
                {
                    kvp.Value.ProcessDatagram(datagram);
                    return;
                }
            }
        }

        public void StopListening()
        {
            lock (_socketLock)
            {
                StopListeningInternal();
            }
        }

        private void StopListeningInternal()
        {
            _isListening = false;
            try
            {
                _cts?.Cancel();
            }
            catch { }

            try
            {
                _udpClient?.Close();
                _udpClient?.Dispose();
            }
            catch { }

            _udpClient = null;
            _cts?.Dispose();
            _cts = null;
            _receiveTask = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopListening();
            foreach (var ch in _channels.Values)
            {
                ch.Dispose();
            }
            _channels.Clear();
            _joinedMulticastGroups.Clear();
        }

        #region Nested DemuxChannel (implements IRtpStreamReceiver)

        /// <summary>
        /// Channel receiver providing isolated frame reassembly, double-buffered WriteableBitmap rendering,
        /// and FPS telemetry for an individual encoder card.
        /// </summary>
        public sealed class DemuxChannel : IRtpStreamReceiver
        {
            private readonly UnifiedRtpDemuxReceiver _parent;
            private readonly ScanlineFrameReassembler _reassembler = new(allowPartialFrames: true);
            private readonly Queue<long> _frameTimestamps = new();
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private readonly object _fpsLock = new();

            private WriteableBitmap? _bitmap;
            private int _bitmapWidth;
            private int _bitmapHeight;
            private int _isRendering; // 0 = idle, 1 = rendering on UI thread

            private int _receivedPacketsCount;
            private int _receivedFramesCount;
            private int _droppedFramesCount;
            private bool _isListening;
            private bool _disposed;

            public string UnicastIp { get; }
            public string MulticastIp { get; set; }
            public int Port => _parent.Port;

            public event Action<byte[], int, int>? FrameReady;
            public event Action<double>? FpsUpdated;
            public event Action<WriteableBitmap>? BitmapUpdated;

            public double CurrentFps { get; private set; }
            public int ReceivedPacketsCount => _receivedPacketsCount;
            public int ReceivedFramesCount => _receivedFramesCount;
            public int DroppedFramesCount => _droppedFramesCount;
            public bool IsListening => _isListening;
            public WriteableBitmap? Bitmap => _bitmap;

            public DemuxChannel(UnifiedRtpDemuxReceiver parent, string unicastIp, string multicastIp)
            {
                _parent = parent ?? throw new ArgumentNullException(nameof(parent));
                UnicastIp = unicastIp;
                MulticastIp = multicastIp;
            }

            public void StartListening(string multicastIp, int port, string localInterfaceIp = "")
            {
                if (!string.IsNullOrEmpty(multicastIp))
                {
                    MulticastIp = multicastIp;
                    _parent.EnsureMulticastGroupJoined(multicastIp);
                }

                _isListening = true;
                _parent.EnsureStarted(port > 0 ? port : 5000);
            }

            public void StopListening()
            {
                _isListening = false;
                lock (_fpsLock)
                {
                    _frameTimestamps.Clear();
                    CurrentFps = 0.0;
                }
                FpsUpdated?.Invoke(0.0);
            }

            public void ProcessDatagram(ReadOnlySpan<byte> datagram)
            {
                Interlocked.Increment(ref _receivedPacketsCount);

                if (RtpHeader.TryParse(datagram, out var rtpHeader))
                {
                    var frame = _reassembler.ProcessPacket(in rtpHeader);
                    if (frame != null)
                    {
                        Interlocked.Increment(ref _receivedFramesCount);
                        UpdateFps();

                        // Notify raw RGB listeners if attached
                        var frameReadyHandler = FrameReady;
                        if (frameReadyHandler != null)
                        {
                            byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(frame.YuvData, frame.Width, frame.Height);
                            frameReadyHandler.Invoke(rgb, frame.Width, frame.Height);
                        }

                        // Submit to UI WriteableBitmap
                        SubmitFrameToUi(frame);
                    }
                }
            }

            private void SubmitFrameToUi(AssembledFrame frame)
            {
                if (Interlocked.CompareExchange(ref _isRendering, 1, 0) != 0)
                {
                    Interlocked.Increment(ref _droppedFramesCount);
                    return;
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                    {
                        try
                        {
                            RenderFrameToBitmap(frame.YuvData, frame.Width, frame.Height);
                        }
                        finally
                        {
                            Volatile.Write(ref _isRendering, 0);
                        }
                    }));
                }
                else
                {
                    Volatile.Write(ref _isRendering, 0);
                }
            }

            private void RenderFrameToBitmap(byte[] yuvData, int width, int height)
            {
                if (_bitmap == null || _bitmapWidth != width || _bitmapHeight != height)
                {
                    _bitmapWidth = width;
                    _bitmapHeight = height;
                    _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                    BitmapUpdated?.Invoke(_bitmap);
                }

                _bitmap.Lock();
                try
                {
                    unsafe
                    {
                        byte* pBackBuffer = (byte*)_bitmap.BackBuffer;
                        int stride = _bitmap.BackBufferStride;

                        fixed (byte* pYuv = yuvData)
                        {
                            Yuv422Rasterizer.ConvertYuv422ToBgr24Stride(pYuv, pBackBuffer, width, height, stride);
                        }
                    }

                    _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                }
                finally
                {
                    _bitmap.Unlock();
                }
            }

            private void UpdateFps()
            {
                lock (_fpsLock)
                {
                    long now = _stopwatch.ElapsedMilliseconds;
                    _frameTimestamps.Enqueue(now);

                    while (_frameTimestamps.Count > 0 && now - _frameTimestamps.Peek() > 1000)
                    {
                        _frameTimestamps.Dequeue();
                    }

                    CurrentFps = _frameTimestamps.Count;
                }

                FpsUpdated?.Invoke(CurrentFps);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                StopListening();
                _reassembler.Reset();
                _bitmap = null;
            }
        }

        #endregion
    }
}
