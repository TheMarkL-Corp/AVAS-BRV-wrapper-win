using System;
using System.Buffers.Binary;

namespace E2ETests.Harness
{
    public static class RtpPacketParser
    {
        public const int MinimumHeaderSize = 20;

        public static bool TryParse(ReadOnlySpan<byte> buffer, out RtpVideoHeader header)
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
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(14, 2));
            ushort rawLine = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(16, 2));
            ushort lineNo = (ushort)(rawLine & 0x7FFF); // mask out Field bit
            ushort rawOffset = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(18, 2));
            ushort offset = (ushort)(rawOffset & 0x7FFF); // mask out Continuation bit

            if (buffer.Length < MinimumHeaderSize + length) return false;

            header = new RtpVideoHeader(
                version,
                marker,
                payloadType,
                seqNo,
                timestamp,
                ssrc,
                length,
                lineNo,
                offset,
                buffer.Slice(MinimumHeaderSize, length));

            return true;
        }
    }
}
