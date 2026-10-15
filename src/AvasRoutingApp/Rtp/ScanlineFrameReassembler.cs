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
        private int _maxSeenLineNo = -1;
        private readonly object _lock = new();

        public bool AllowPartialFrames { get; set; }

        public ScanlineFrameReassembler(bool allowPartialFrames = false)
        {
            AllowPartialFrames = allowPartialFrames;
        }

        public int FramesCompleted { get; private set; }
        public int FramesDropped { get; private set; }

        /// <summary>
        /// Ingests a single RTP packet. If the packet completes a frame (Marker == 1 and lines present),
        /// returns the assembled frame. Otherwise returns null.
        /// </summary>
        public AssembledFrame? ProcessPacket(in RtpHeader packet)
        {
            lock (_lock)
            {
                // Calculate timestamp distance (tolerates 32-bit wrap)
                long tsDiff = _scanlines.Count > 0 ? Math.Abs((long)packet.Timestamp - _currentTimestamp) : 0;

                // Handle duplicate scanlines:
                // If this scanline was already received and timestamp is within current frame tolerance (<= 1000),
                // ignore duplicate datagram without nuking the frame in progress.
                if (_scanlines.ContainsKey(packet.LineNo))
                {
                    if (tsDiff < 1000 && packet.LineNo <= _maxSeenLineNo && !(_maxSeenLineNo >= 10 && packet.LineNo <= 2 && _maxSeenLineNo - packet.LineNo > 10))
                    {
                        return null; // Ignore duplicate packet
                    }
                }

                // Detect new frame boundary before previous finished:
                // Triggered if line number wraps backwards to start of frame (e.g. from >10 to 0..2)
                // or if a timestamp jump occurred (>= 1000).
                // Note: Advantech AVAS-223 hardware increments packet timestamp by 1 per packet within the same frame.
                bool isLineWrap = _scanlines.Count > 0 && _maxSeenLineNo >= 10 && packet.LineNo <= 2 && (_maxSeenLineNo - packet.LineNo > 10);
                bool isTimestampLeap = _scanlines.Count > 0 && tsDiff >= 1000;
                bool isNewFrame = isLineWrap || isTimestampLeap;

                AssembledFrame? recoveredFrame = null;
                if (isNewFrame)
                {
                    if (AllowPartialFrames && _scanlines.Count >= 10 && _maxSeenLineNo > 0 && _scanlineLength > 0)
                    {
                        int expHeight = _maxSeenLineNo + 1;
                        int expWidth = _scanlineLength / 2;
                        if (expHeight > 0 && expWidth > 0)
                        {
                            byte[] fullYuv = new byte[expWidth * expHeight * 2];
                            byte[]? lastValidLine = null;
                            for (ushort l = 0; l < expHeight; l++)
                            {
                                if (_scanlines.TryGetValue(l, out var line))
                                {
                                    Buffer.BlockCopy(line, 0, fullYuv, l * line.Length, Math.Min(line.Length, fullYuv.Length - (l * line.Length)));
                                    lastValidLine = line;
                                }
                                else if (lastValidLine != null)
                                {
                                    Buffer.BlockCopy(lastValidLine, 0, fullYuv, l * lastValidLine.Length, Math.Min(lastValidLine.Length, fullYuv.Length - (l * lastValidLine.Length)));
                                }
                            }
                            FramesCompleted++;
                            recoveredFrame = new AssembledFrame(fullYuv, expWidth, expHeight, _currentTimestamp, _currentSsrc);
                        }
                    }

                    if (recoveredFrame == null)
                    {
                        FramesDropped++;
                    }
                    _scanlines.Clear();
                    _maxSeenLineNo = -1;
                }

                _currentTimestamp = packet.Timestamp;
                _currentSsrc = packet.Ssrc;
                _scanlineLength = packet.Length;
                _maxSeenLineNo = Math.Max(_maxSeenLineNo, (int)packet.LineNo);

                // Direct slotting into scanline dictionary (absorbs jitter & out-of-order packets)
                byte[] lineData = packet.Payload.ToArray();
                _scanlines[packet.LineNo] = lineData;

                if (recoveredFrame != null)
                {
                    return recoveredFrame;
                }

                // Frame boundary triggered by Marker bit
                if (packet.Marker)
                {
                    int expectedHeight = packet.LineNo + 1;
                    int expectedWidth = _scanlineLength / 2;

                    // Verify completeness: all scanlines from 0 to expectedHeight - 1 must exist (or partial allowed)
                    bool isComplete = true;
                    for (ushort l = 0; l < expectedHeight; l++)
                    {
                        if (!_scanlines.ContainsKey(l))
                        {
                            isComplete = false;
                            break;
                        }
                    }

                    if ((isComplete || (AllowPartialFrames && _scanlines.Count > 0)) && expectedHeight > 0 && expectedWidth > 0)
                    {
                        byte[] fullYuv = new byte[expectedWidth * expectedHeight * 2];
                        byte[]? lastValidLine = null;
                        for (ushort l = 0; l < expectedHeight; l++)
                        {
                            if (_scanlines.TryGetValue(l, out var line))
                            {
                                Buffer.BlockCopy(line, 0, fullYuv, l * line.Length, Math.Min(line.Length, fullYuv.Length - (l * line.Length)));
                                lastValidLine = line;
                            }
                            else if (AllowPartialFrames && lastValidLine != null)
                            {
                                Buffer.BlockCopy(lastValidLine, 0, fullYuv, l * lastValidLine.Length, Math.Min(lastValidLine.Length, fullYuv.Length - (l * lastValidLine.Length)));
                            }
                        }

                        FramesCompleted++;
                        _scanlines.Clear();
                        _currentTimestamp = 0;
                        _maxSeenLineNo = -1;

                        return new AssembledFrame(fullYuv, expectedWidth, expectedHeight, packet.Timestamp, _currentSsrc);
                    }
                    else
                    {
                        // Incomplete frame: drop and reset
                        FramesDropped++;
                        _scanlines.Clear();
                        _currentTimestamp = 0;
                        _maxSeenLineNo = -1;
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
                _maxSeenLineNo = -1;
            }
        }
    }
}
