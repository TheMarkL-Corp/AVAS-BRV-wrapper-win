using System;
using System.Collections.Generic;

namespace AvasRoutingApp.Rtp
{
    /// <summary>
    /// Represents a fully assembled progressive video frame in uncompressed YUV 4:2:2 format.
    /// </summary>
    public class AssembledFrame
    {
        public byte[] YuvData { get; }
        public int Width { get; }
        public int Height { get; }
        public uint Timestamp { get; }
        public uint Ssrc { get; }

        public AssembledFrame(byte[] yuvData, int width, int height, uint timestamp, uint ssrc)
        {
            YuvData = yuvData ?? throw new ArgumentNullException(nameof(yuvData));
            Width = width;
            Height = height;
            Timestamp = timestamp;
            Ssrc = ssrc;
        }
    }

    /// <summary>
    /// Reassembles progressive scanlines from RTP packets into complete YUV 4:2:2 frames.
    /// Employs direct O(1) slotting based on LineNo to seamlessly absorb out-of-order packet arrivals.
    /// Validates frame completeness upon packet marker boundary (Marker == 1).
    /// </summary>
    public class ScanlineFrameReassembler
    {
        private readonly Dictionary<ushort, byte[]> _scanlines = new();
        private uint _currentTimestamp = 0;
        private uint _currentSsrc = 0;
        private int _scanlineLength = 0;
        private int _lastSeenLineNo = -1;
        private readonly object _lock = new();

        public int FramesCompleted { get; private set; }
        public int FramesDropped { get; private set; }

        /// <summary>
        /// Ingests a single RTP packet. If the packet completes a frame (Marker == 1 and all lines present),
        /// returns the assembled frame. Otherwise returns null.
        /// </summary>
        public AssembledFrame? ProcessPacket(in RtpHeader packet)
        {
            lock (_lock)
            {
                // Detect new frame boundary before previous finished:
                // Triggered if line 0 arrives when line 0 was already received, or if a large timestamp jump occurred (> 1000).
                // Note: Advantech AVAS-223 hardware increments packet timestamp by 1 per packet within the same frame.
                bool isNewFrame = _scanlines.Count > 0 &&
                    ((_scanlines.ContainsKey(packet.LineNo) && packet.LineNo == 0) ||
                     Math.Abs((long)packet.Timestamp - _currentTimestamp) > 1000);

                if (isNewFrame)
                {
                    FramesDropped++;
                    _scanlines.Clear();
                    _lastSeenLineNo = -1;
                }

                _currentTimestamp = packet.Timestamp;
                _currentSsrc = packet.Ssrc;
                _scanlineLength = packet.Length;
                _lastSeenLineNo = packet.LineNo;

                // Direct slotting into scanline dictionary (absorbs jitter & out-of-order packets)
                byte[] lineData = packet.Payload.ToArray();
                _scanlines[packet.LineNo] = lineData;

                // Frame boundary triggered by Marker bit
                if (packet.Marker)
                {
                    int expectedHeight = packet.LineNo + 1;
                    int expectedWidth = _scanlineLength / 2;

                    // Verify completeness: all scanlines from 0 to expectedHeight - 1 must exist
                    bool isComplete = true;
                    for (ushort l = 0; l < expectedHeight; l++)
                    {
                        if (!_scanlines.ContainsKey(l))
                        {
                            isComplete = false;
                            break;
                        }
                    }

                    if (isComplete && expectedHeight > 0 && expectedWidth > 0)
                    {
                        byte[] fullYuv = new byte[expectedWidth * expectedHeight * 2];
                        for (ushort l = 0; l < expectedHeight; l++)
                        {
                            byte[] line = _scanlines[l];
                            Buffer.BlockCopy(line, 0, fullYuv, l * line.Length, line.Length);
                        }

                        FramesCompleted++;
                        _scanlines.Clear();
                        _currentTimestamp = 0;
                        _lastSeenLineNo = -1;

                        return new AssembledFrame(fullYuv, expectedWidth, expectedHeight, packet.Timestamp, _currentSsrc);
                    }
                    else
                    {
                        // Incomplete frame: drop and reset
                        FramesDropped++;
                        _scanlines.Clear();
                        _currentTimestamp = 0;
                        _lastSeenLineNo = -1;
                        return null;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Resets the reassembler buffer and state, clearing any partially assembled frames.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _scanlines.Clear();
                _currentTimestamp = 0;
                _currentSsrc = 0;
                _scanlineLength = 0;
                _lastSeenLineNo = -1;
            }
        }
    }
}
