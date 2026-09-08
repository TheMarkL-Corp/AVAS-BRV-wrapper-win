using System;

namespace AvasRoutingApp.Rtp
{
    /// <summary>
    /// High-performance RFC 3550 RTP and RFC 4175 uncompressed video packet parser.
    /// Validates 20-byte packet headers and extracts scanline payload data.
    /// </summary>
    public static class RtpPacketParser
    {
        public const int MinimumHeaderSize = RtpHeader.MinimumHeaderSize;

        /// <summary>
        /// Parses and validates an RTP datagram buffer into a structured RtpHeader.
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> buffer, out RtpHeader header)
        {
            return RtpHeader.TryParse(buffer, out header);
        }
    }
}
