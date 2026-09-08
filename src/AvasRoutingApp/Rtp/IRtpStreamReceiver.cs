using System;
using System.Windows.Media.Imaging;

namespace AvasRoutingApp.Rtp
{
    /// <summary>
    /// Service contract for receiving and processing live RTP multicast video streams.
    /// Conforms to PROJECT.md § Interface Contracts and E2ETests harness.
    /// </summary>
    public interface IRtpStreamReceiver : IDisposable
    {
        /// <summary>
        /// Begins listening for RTP packets on the specified multicast group and port.
        /// </summary>
        void StartListening(string multicastIp, int port, string localInterfaceIp = "");

        /// <summary>
        /// Ceases multicast listening, drops IGMP memberships, and cleans up sockets.
        /// </summary>
        void StopListening();

        /// <summary>
        /// Ingests a raw datagram span directly (used for network input and unit testing).
        /// </summary>
        void ProcessDatagram(ReadOnlySpan<byte> datagram);

        /// <summary>
        /// Raised when a complete frame is reassembled and converted to RGB24.
        /// </summary>
        event Action<byte[], int, int>? FrameReady;

        /// <summary>
        /// Raised when a new rolling FPS calculation is available.
        /// </summary>
        event Action<double>? FpsUpdated;

        /// <summary>
        /// Raised when the WriteableBitmap is instantiated or resized.
        /// </summary>
        event Action<WriteableBitmap>? BitmapUpdated;

        /// <summary>
        /// Current live rolling frames-per-second metric.
        /// </summary>
        double CurrentFps { get; }

        /// <summary>
        /// Indicates whether the receiver is actively listening on UDP sockets.
        /// </summary>
        bool IsListening { get; }
    }
}
