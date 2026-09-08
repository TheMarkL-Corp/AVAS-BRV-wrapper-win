using System;
using System.Collections.Generic;

namespace E2ETests.Harness
{
    public class AssembledFrame
    {
        public byte[] YuvData { get; }
        public int Width { get; }
        public int Height { get; }
        public uint Timestamp { get; }
        public uint Ssrc { get; }

        public AssembledFrame(byte[] yuvData, int width, int height, uint timestamp, uint ssrc)
        {
            YuvData = yuvData;
            Width = width;
            Height = height;
            Timestamp = timestamp;
            Ssrc = ssrc;
        }
    }

    public class ScanlineFrameReassembler
    {
        private readonly Dictionary<ushort, byte[]> _scanlines = new();
        private uint _currentTimestamp = 0;
        private uint _currentSsrc = 0;
        private int _scanlineLength = 0;
        private readonly object _lock = new();

        public int FramesCompleted { get; private set; }
        public int FramesDropped { get; private set; }

        public AssembledFrame? ProcessPacket(in RtpVideoHeader packet)
        {
            lock (_lock)
            {
                // New frame timestamp arrived without finishing previous
                if (_currentTimestamp != 0 && packet.Timestamp != _currentTimestamp)
                {
                    if (_scanlines.Count > 0)
                    {
                        FramesDropped++;
                    }
                    _scanlines.Clear();
                }

                _currentTimestamp = packet.Timestamp;
                _currentSsrc = packet.Ssrc;
                _scanlineLength = packet.Length;

                // Slot line data
                byte[] lineData = packet.Payload.ToArray();
                _scanlines[packet.LineNo] = lineData;

                // Check for frame completion
                if (packet.Marker)
                {
                    int expectedHeight = packet.LineNo + 1;
                    int expectedWidth = _scanlineLength / 2;

                    // Verify all lines present
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

                        return new AssembledFrame(fullYuv, expectedWidth, expectedHeight, packet.Timestamp, _currentSsrc);
                    }
                    else
                    {
                        FramesDropped++;
                        _scanlines.Clear();
                        _currentTimestamp = 0;
                        return null;
                    }
                }

                return null;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _scanlines.Clear();
                _currentTimestamp = 0;
            }
        }
    }
}
