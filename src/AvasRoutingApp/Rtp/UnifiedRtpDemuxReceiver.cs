using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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

        public static readonly int[] DefaultPorts = new[] { 5000, 5004 };

        private readonly ConcurrentDictionary<string, DemuxChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DemuxChannel> _aliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _joinedMulticastGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IPAddress?> _groupLocalNics = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _socketLock = new();

        private readonly List<UdpClient> _udpClients = new();
        private readonly List<int> _listeningPorts = new();
        private CancellationTokenSource? _cts;
        private int _port = 5000;
        private bool _isListening;
        private bool _disposed;
        private long _unmatchedPacketsCount;

        public int Port
        {
            get
            {
                lock (_socketLock)
                {
                    return _listeningPorts.Count > 0 ? _listeningPorts[0] : _port;
                }
            }
        }

        public IReadOnlyList<int> ListeningPorts
        {
            get
            {
                lock (_socketLock)
                {
                    return _listeningPorts.ToArray();
                }
            }
        }

        public bool IsListening => _isListening;
        public int ChannelCount => _channels.Count;
        public int AliasCount => _aliases.Count;
        public long UnmatchedPacketsCount => Interlocked.Read(ref _unmatchedPacketsCount);

        public void ResetStats()
        {
            Interlocked.Exchange(ref _unmatchedPacketsCount, 0);
        }

        public UnifiedRtpDemuxReceiver(int port = 5000)
        {
            _port = port;
        }

        /// <summary>
        /// Gets or creates a channel receiver associated with an encoder's unicast IP, multicast group,
        /// and optional secondary aliases (such as companion MAC or 10G SFP IP).
        /// </summary>
        public IRtpStreamReceiver GetOrCreateChannel(string unicastIp, string multicastIp = "")
        {
            return GetOrCreateChannel(unicastIp, multicastIp, (IEnumerable<string>?)null);
        }

        /// <summary>
        /// Gets or creates a channel receiver associated with an encoder's unicast IP, multicast group,
        /// and optional secondary aliases (such as companion MAC or 10G SFP IP).
        /// </summary>
        public IRtpStreamReceiver GetOrCreateChannel(string unicastIp, string multicastIp, IEnumerable<string>? aliases)
        {
            if (string.IsNullOrWhiteSpace(unicastIp))
            {
                unicastIp = "0.0.0.0";
            }
            unicastIp = unicastIp.Trim();

            var channel = _channels.GetOrAdd(unicastIp, ip => new DemuxChannel(this, ip, multicastIp));
            if (!string.IsNullOrWhiteSpace(multicastIp))
            {
                channel.MulticastIp = multicastIp;
                EnsureMulticastGroupJoined(multicastIp);
            }

            if (aliases != null)
            {
                foreach (var alias in aliases)
                {
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        RegisterAlias(alias.Trim(), channel);
                    }
                }
            }

            return channel;
        }

        /// <summary>
        /// Registers a secondary IP, companion MAC, or alternative endpoint to route to the channel.
        /// </summary>
        public void RegisterAlias(string alias, string primaryUnicastIp)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(primaryUnicastIp)) return;
            string key = primaryUnicastIp.Trim();
            if (_channels.TryGetValue(key, out var channel))
            {
                RegisterAlias(alias.Trim(), channel);
            }
        }

        /// <summary>
        /// Registers an alias directly to an existing DemuxChannel instance.
        /// </summary>
        public void RegisterAlias(string alias, DemuxChannel channel)
        {
            if (string.IsNullOrWhiteSpace(alias) || channel == null) return;
            string cleanAlias = alias.Trim();
            _aliases[cleanAlias] = channel;
            AppLogger.Debug("UnifiedDemux", $"Registered alias '{cleanAlias}' -> channel '{channel.UnicastIp}'");
        }

        /// <summary>
        /// Removes and disposes an existing channel for the specified unicast IP and clears its aliases.
        /// </summary>
        public void RemoveChannel(string unicastIp)
        {
            if (string.IsNullOrWhiteSpace(unicastIp)) return;
            string key = unicastIp.Trim();
            if (_channels.TryRemove(key, out var channel))
            {
                foreach (var kvp in _aliases)
                {
                    if (ReferenceEquals(kvp.Value, channel))
                    {
                        _aliases.TryRemove(kvp.Key, out _);
                    }
                }
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
        /// Starts the unified UDP listener on the specified port if not already active.
        /// </summary>
        public void EnsureStarted(int port)
        {
            EnsureStarted(new[] { port });
        }

        /// <summary>
        /// Starts the unified UDP listeners on the specified ports (or DefaultPorts 5000 and 5004) if not already active.
        /// </summary>
        public void EnsureStarted(IEnumerable<int>? ports = null)
        {
            lock (_socketLock)
            {
                if (_isListening) return;

                var targetPorts = (ports != null && ports.Any()) ? ports.Distinct().ToArray() : DefaultPorts;
                _cts = new CancellationTokenSource();
                _isListening = true;
                _udpClients.Clear();
                _listeningPorts.Clear();

                try
                {
                    foreach (int p in targetPorts)
                    {
                        try
                        {
                            var client = new UdpClient();
                            client.ExclusiveAddressUse = false;
                            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            client.Client.ReceiveBufferSize = 8 * 1024 * 1024; // 8MB buffer

                            var localEp = new IPEndPoint(IPAddress.Any, p);
                            client.Client.Bind(localEp);

                            _udpClients.Add(client);
                            _listeningPorts.Add(p);

                            var token = _cts.Token;
                            Task.Run(() => ReceiveLoopAsync(client, token), token);
                            AppLogger.Info("UnifiedDemux", $"Unified RTP Demux listener active on port {p}");
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("UnifiedDemux", $"Failed to bind UDP port {p}: {ex.Message}");
                        }
                    }

                    if (_udpClients.Count == 0)
                    {
                        throw new InvalidOperationException("Could not bind any UDP listening ports.");
                    }

                    _port = _listeningPorts[0];

                    // Re-join any previously tracked multicast groups across all clients
                    foreach (var mcastIp in _joinedMulticastGroups)
                    {
                        _groupLocalNics.TryGetValue(mcastIp, out var localNic);
                        JoinMulticastGroupInternal(mcastIp, localNic);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("UnifiedDemux", "Failed to initialize unified RTP sockets", ex);
                    StopListeningInternal();
                    throw;
                }
            }
        }

        /// <summary>
        /// Joins the specified multicast group across the specified local NIC or all available local IPv4 network interfaces.
        /// </summary>
        public void EnsureMulticastGroupJoined(string multicastIp, string localInterfaceIp = "")
        {
            IPAddress? nicAddr = null;
            if (!string.IsNullOrWhiteSpace(localInterfaceIp) && IPAddress.TryParse(localInterfaceIp, out var parsed))
            {
                nicAddr = parsed;
            }
            EnsureMulticastGroupJoined(multicastIp, nicAddr);
        }

        /// <summary>
        /// Joins the specified multicast group across the specified local NIC address.
        /// </summary>
        public void EnsureMulticastGroupJoined(string multicastIp, IPAddress? localNicAddress)
        {
            if (string.IsNullOrWhiteSpace(multicastIp)) return;
            lock (_socketLock)
            {
                if (localNicAddress != null)
                {
                    _groupLocalNics[multicastIp] = localNicAddress;
                }

                if (!_joinedMulticastGroups.Contains(multicastIp))
                {
                    _joinedMulticastGroups.Add(multicastIp);
                }

                if (_isListening && _udpClients.Count > 0)
                {
                    _groupLocalNics.TryGetValue(multicastIp, out var nic);
                    JoinMulticastGroupInternal(multicastIp, nic ?? localNicAddress);
                }
            }
        }

        private void JoinMulticastGroupInternal(string multicastIp, IPAddress? localNicAddress = null)
        {
            if (_udpClients.Count == 0) return;
            if (!IPAddress.TryParse(multicastIp, out var mcastAddr)) return;
            byte firstByte = mcastAddr.GetAddressBytes()[0];
            if (firstByte < 224 || firstByte > 239) return;

            foreach (var client in _udpClients)
            {
                // Direct binding: If a specific local NIC IP was designated, prioritize it
                if (localNicAddress != null)
                {
                    try
                    {
                        client.JoinMulticastGroup(mcastAddr, localNicAddress);
                        AppLogger.Info("UnifiedDemux", $"Joined multicast {multicastIp} on dedicated NIC {localNicAddress}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("UnifiedDemux", $"Failed to join multicast {multicastIp} on dedicated NIC {localNicAddress}: {ex.Message}. Falling back to multi-adapter sweep.");
                    }
                }

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
                                client.JoinMulticastGroup(mcastAddr, addr);
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
                        client.JoinMulticastGroup(mcastAddr);
                    }
                    catch (SocketException)
                    {
                        // Allow unjoined listen in loopback or virtualized environments
                    }
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
                    if (result.Buffer == null || result.Buffer.Length < RtpHeader.MinimumHeaderSize)
                    {
                        continue;
                    }

                    string senderIp = result.RemoteEndPoint.Address.ToString();

                    // Source-IP Whitelist Filter (REA Report 06):
                    // Discard immediately before parsing the 20-byte RFC 4175 header,
                    // allocating scanline buffers, or processing frames if remote IP is not whitelisted.
                    if (!TryResolveChannel(senderIp, out var channel))
                    {
                        Interlocked.Increment(ref _unmatchedPacketsCount);
                        continue;
                    }

                    channel.ProcessDatagram(result.Buffer);
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
        /// Attempts to resolve an incoming sender IP address against active monitored channels or registered aliases.
        /// Implements the source-IP whitelist filter defined in REA Report 06 (Approach 1).
        /// </summary>
        public bool TryResolveChannel(string senderIp, [NotNullWhen(true)] out DemuxChannel? channel)
        {
            if (string.IsNullOrWhiteSpace(senderIp))
            {
                channel = null;
                return false;
            }

            string cleanIp = senderIp.Trim();

            // 1. Direct channel lookup (O(1))
            if (_channels.TryGetValue(cleanIp, out channel))
            {
                return true;
            }

            // 2. Direct alias lookup (O(1))
            if (_aliases.TryGetValue(cleanIp, out channel))
            {
                return true;
            }

            // 3. Subnet /24 fallback: Match ONLY if exactly one registered channel belongs to this /24 subnet.
            // If multiple channels share the subnet, we cannot unambiguously infer the target channel.
            if (IPAddress.TryParse(cleanIp, out var senderAddr) && senderAddr.AddressFamily == AddressFamily.InterNetwork)
            {
                string senderPrefix = GetSubnet24Prefix(cleanIp);
                DemuxChannel? matchCandidate = null;
                int matchCount = 0;

                foreach (var kvp in _channels)
                {
                    if (GetSubnet24Prefix(kvp.Key) == senderPrefix)
                    {
                        matchCandidate = kvp.Value;
                        matchCount++;
                    }
                }

                if (matchCount == 1 && matchCandidate != null)
                {
                    // Auto-alias this IP for future zero-latency O(1) routing
                    _aliases[cleanIp] = matchCandidate;
                    channel = matchCandidate;
                    return true;
                }
            }

            // Unmatched sender IP -> drop datagram
            channel = null;
            return false;
        }

        /// <summary>
        /// Resolves an IP or registered alias to an IRtpStreamReceiver channel, if present.
        /// </summary>
        public bool TryGetChannel(string unicastOrAliasIp, [NotNullWhen(true)] out IRtpStreamReceiver? channel)
        {
            if (TryResolveChannel(unicastOrAliasIp, out var demuxChannel))
            {
                channel = demuxChannel;
                return true;
            }
            channel = null;
            return false;
        }

        /// <summary>
        /// Demultiplexes a datagram to the appropriate channel based on sender IP address or registered alias.
        /// Returns true if accepted and routed; false if discarded as an unregistered source IP.
        /// </summary>
        public bool DemuxPacket(string senderIp, ReadOnlySpan<byte> datagram)
        {
            if (TryResolveChannel(senderIp, out var channel))
            {
                channel.ProcessDatagram(datagram);
                return true;
            }

            Interlocked.Increment(ref _unmatchedPacketsCount);
            return false;
        }

        private static string GetSubnet24Prefix(string ip)
        {
            int lastDot = ip.LastIndexOf('.');
            return lastDot > 0 ? ip.Substring(0, lastDot) : ip;
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

            foreach (var client in _udpClients)
            {
                try
                {
                    foreach (var mcastIp in _joinedMulticastGroups)
                    {
                        if (IPAddress.TryParse(mcastIp, out var mcastAddr))
                        {
                            try
                            {
                                client.DropMulticastGroup(mcastAddr);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                try
                {
                    client.Close();
                    client.Dispose();
                }
                catch { }
            }

            _udpClients.Clear();
            _listeningPorts.Clear();

            _cts?.Dispose();
            _cts = null;
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
            _aliases.Clear();
            _groupLocalNics.Clear();
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
                    _parent.EnsureMulticastGroupJoined(multicastIp, localInterfaceIp);
                }

                _isListening = true;
                if (port > 0 && port != 5000 && port != 5004)
                {
                    _parent.EnsureStarted(new[] { port });
                }
                else
                {
                    _parent.EnsureStarted(DefaultPorts);
                }
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
