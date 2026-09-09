using System;
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

namespace AvasRoutingApp.Rtp
{
    /// <summary>
    /// Asynchronous UDP multicast receiver and frame processor for SDVoE uncompressed video.
    /// Manages network sockets, joins IGMP multicast groups, parses RFC 3550/4175 packets,
    /// reassembles scanlines, computes rolling FPS telemetry, and renders to a double-buffered WriteableBitmap
    /// with backpressure protection and clean teardown.
    /// </summary>
    public class RtpMulticastReceiver : IRtpStreamReceiver, IAsyncDisposable, IDisposable
    {
        private UdpClient? _udpClient;
        private IPAddress? _joinedMulticastAddress;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;
        private readonly ScanlineFrameReassembler _reassembler = new();
        private readonly Queue<long> _frameTimestamps = new();
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _fpsLock = new();

        private WriteableBitmap? _bitmap;
        private int _bitmapWidth;
        private int _bitmapHeight;
        private int _isRendering; // 0 = idle, 1 = rendering on UI thread (backpressure gate)

        private int _receivedPacketsCount;
        private int _receivedFramesCount;
        private int _droppedFramesCount;
        private bool _isListening;
        private bool _disposed;

        public event Action<byte[], int, int>? FrameReady;
        public event Action<double>? FpsUpdated;
        public event Action<WriteableBitmap>? BitmapUpdated;
        public event Action<IntPtr, int, int, int>? NativeFrameReady;

        public double CurrentFps { get; private set; }
        public int ReceivedPacketsCount => _receivedPacketsCount;
        public int ReceivedFramesCount => _receivedFramesCount;
        public int DroppedFramesCount => _droppedFramesCount;
        public bool IsListening => _isListening;
        public string MulticastIp { get; private set; } = string.Empty;
        public int Port { get; private set; }
        public string LocalInterfaceIp { get; private set; } = string.Empty;
        public WriteableBitmap? Bitmap => _bitmap;

        /// <summary>
        /// Starts asynchronous listening on the specified multicast group and UDP port.
        /// </summary>
        public void StartListening(string multicastIp, int port, string localInterfaceIp = "")
        {
            if (_isListening)
            {
                StopListening();
            }

            MulticastIp = multicastIp;
            Port = port;
            LocalInterfaceIp = localInterfaceIp;

            _cts = new CancellationTokenSource();
            _isListening = true;

            try
            {
                _udpClient = new UdpClient();
                _udpClient.ExclusiveAddressUse = false;
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.ReceiveBufferSize = 4 * 1024 * 1024; // 4MB buffer absorbs micro-bursts

                var localEp = new IPEndPoint(IPAddress.Any, port);
                _udpClient.Client.Bind(localEp);

                if (_udpClient.Client.LocalEndPoint is IPEndPoint boundEp)
                {
                    Port = boundEp.Port;
                }

                if (IPAddress.TryParse(multicastIp, out var mcastAddr) &&
                    mcastAddr.GetAddressBytes()[0] >= 224 && mcastAddr.GetAddressBytes()[0] <= 239)
                {
                    _joinedMulticastAddress = mcastAddr;
                    try
                    {
                        if (!string.IsNullOrEmpty(localInterfaceIp) && IPAddress.TryParse(localInterfaceIp, out var localNic))
                        {
                            _udpClient.JoinMulticastGroup(mcastAddr, localNic);
                        }
                        else
                        {
                            // Mirror THUMBNAIL_AVP: join multicast on all active IPv4 interfaces
                            // to guarantee IGMP reports reach the AV network switch across multi-NIC hosts
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
                                _udpClient.JoinMulticastGroup(mcastAddr);
                            }
                        }
                    }
                    catch (SocketException)
                    {
                        // In virtualized CI, offline, or loopback environments without multicast routing table entry,
                        // allow socket to remain bound and listening for datagrams directly.
                    }
                }

                var client = _udpClient;
                var token = _cts.Token;
                _receiveTask = Task.Run(() => ReceiveLoopAsync(client, token), token);
            }
            catch (Exception)
            {
                StopListening();
                throw;
            }
        }

        /// <summary>
        /// Processes a raw datagram span directly into the RTP reassembly pipeline.
        /// </summary>
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

                    // Convert to RGB24 byte array only if listeners are attached (prevents LOH churn)
                    var frameReadyHandler = FrameReady;
                    if (frameReadyHandler != null)
                    {
                        byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(frame.YuvData, frame.Width, frame.Height);
                        frameReadyHandler.Invoke(rgb, frame.Width, frame.Height);
                    }

                    // Dispatch to WriteableBitmap on UI thread with backpressure drop
                    SubmitFrameToUi(frame);
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
                        ProcessDatagram(result.Buffer);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch { break; }
            }
        }

        private void SubmitFrameToUi(AssembledFrame frame)
        {
            // Backpressure check: if UI thread is still rendering previous frame, drop this frame
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
                // Headless/test environment without WPF application dispatcher
                Volatile.Write(ref _isRendering, 0);
            }
        }

        private void RenderFrameToBitmap(byte[] yuvData, int width, int height)
        {
            // Reallocate WriteableBitmap only if dimensions change or initial allocation
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

                    NativeFrameReady?.Invoke(_bitmap.BackBuffer, width, height, stride);
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

                // Retain timestamps within the last 1500ms
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

        /// <summary>
        /// Stops listening, leaves the multicast group, and disposes the socket and cancellation token.
        /// </summary>
        public void StopListening()
        {
            if (!_isListening) return;
            _isListening = false;

            try { _cts?.Cancel(); } catch { }

            if (_udpClient != null && _joinedMulticastAddress != null)
            {
                try
                {
                    _udpClient.DropMulticastGroup(_joinedMulticastAddress);
                }
                catch { }
                _joinedMulticastAddress = null;
            }

            try { _udpClient?.Close(); } catch { }
            try { _udpClient?.Dispose(); } catch { }
            _udpClient = null;

            try { _cts?.Dispose(); } catch { }
            _cts = null;

            _reassembler.Reset();

            lock (_fpsLock)
            {
                _frameTimestamps.Clear();
                CurrentFps = 0.0;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopListening();
            _bitmap = null;
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            StopListening();
            if (_receiveTask != null)
            {
                try { await _receiveTask; } catch { }
            }
            _bitmap = null;
            GC.SuppressFinalize(this);
        }
    }
}
