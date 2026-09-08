using System;
using System.Buffers.Binary;

namespace AvasRoutingApp.Rtp
{
    /// <summary>
    /// Represents the 20-byte combined RFC 3550 RTP and RFC 4175 video payload header.
    /// Uses ref struct for zero-allocation stack semantics.
    /// </summary>
    public readonly ref struct RtpHeader
    {
        public const int MinimumHeaderSize = 20;

        public readonly byte Version;
        public readonly bool Marker;
        public readonly byte PayloadType;
        public readonly ushort SequenceNumber;
        public readonly uint Timestamp;
        public readonly uint Ssrc;
        public readonly ushort ExtendedSequenceNumber;
        public readonly ushort Length;
        public readonly ushort LineNo;
        public readonly ushort Offset;
        public readonly ReadOnlySpan<byte> Payload;

        public RtpHeader(
            byte version,
            bool marker,
            byte payloadType,
            ushort seqNo,
            uint timestamp,
            uint ssrc,
            ushort length,
            ushort lineNo,
            ushort offset,
            ReadOnlySpan<byte> payload,
            ushort extendedSeqNo = 0)
        {
            Version = version;
            Marker = marker;
            PayloadType = payloadType;
            SequenceNumber = seqNo;
            Timestamp = timestamp;
            Ssrc = ssrc;
            ExtendedSequenceNumber = extendedSeqNo;
            Length = length;
            LineNo = lineNo;
            Offset = offset;
            Payload = payload;
        }

        /// <summary>
        /// Attempts to parse a 20-byte combined RTP + RFC 4175 header and extract scanline payload.
        /// Zero heap allocations.
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> buffer, out RtpHeader header)
        {
            header = default;
            if (buffer.Length < MinimumHeaderSize) return false;

            byte b0 = buffer[0];
            byte version = (byte)(b0 >> 6);
            if (version != 2) return false; // Strict RFC 3550 requirement: V must equal 2

            byte b1 = buffer[1];
            bool marker = (b1 & 0x80) != 0;
            byte payloadType = (byte)(b1 & 0x7F);

            ushort seqNo = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
            uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
            uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4));

            // RFC 4175 Video Payload Header (Bytes 12-19)
            ushort extSeqNo = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(12, 2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(14, 2));
            ushort rawLine = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(16, 2));
            ushort lineNo = (ushort)(rawLine & 0x7FFF); // mask out Field bit
            ushort rawOffset = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(18, 2));
            ushort offset = (ushort)(rawOffset & 0x7FFF); // mask out Continuation bit

            if (buffer.Length < MinimumHeaderSize + length) return false;

            header = new RtpHeader(
                version,
                marker,
                payloadType,
                seqNo,
                timestamp,
                ssrc,
                length,
                lineNo,
                offset,
                buffer.Slice(MinimumHeaderSize, length),
                extSeqNo);

            return true;
        }
    }
}
