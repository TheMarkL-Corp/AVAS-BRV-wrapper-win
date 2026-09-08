using System;

namespace AvasRoutingApp.Rtp
{
    /// <summary>
    /// High-performance YUV 4:2:2 to RGB24 / BGR24 color space rasterizer.
    /// Implements fast Q10 fixed-point integer arithmetic and a 1024-byte branchless clamp LUT.
    /// Supports zero-allocation span-based and unsafe pointer-based blitting directly into WriteableBitmap.
    /// </summary>
    public static class Yuv422Rasterizer
    {
        // 1024-byte clamp LUT centered at offset 384 (covers index range [-384, +639])
        // Any value <= 0 clamps to 0, values >= 255 clamp to 255.
        private static readonly byte[] ClampTable = new byte[1024];

        static Yuv422Rasterizer()
        {
            for (int i = 0; i < 1024; i++)
            {
                int val = i - 384;
                ClampTable[i] = (byte)(val < 0 ? 0 : (val > 255 ? 255 : val));
            }
        }

        /// <summary>
        /// Fast clamping using the 1024-byte clamp LUT.
        /// </summary>
        public static byte Clamp(int val)
        {
            int idx = val + 384;
            if ((uint)idx < 1024) return ClampTable[idx];
            return val < 0 ? (byte)0 : (byte)255;
        }

        /// <summary>
        /// Reference floating-point conversion according to Semtech / SDVoE formula:
        /// R = Y + 1.4065 * (U - 128)
        /// G = Y - 0.3455 * (V - 128) - 0.7169 * (U - 128)
        /// B = Y + 1.7790 * (V - 128)
        /// </summary>
        public static (byte r, byte g, byte b) ConvertPixelFloat(byte y, byte u, byte v)
        {
            double dU = u - 128.0;
            double dV = v - 128.0;

            double r = y + 1.4065 * dU;
            double g = y - 0.3455 * dV - 0.7169 * dU;
            double b = y + 1.7790 * dV;

            byte br = (byte)Math.Clamp((int)Math.Round(r), 0, 255);
            byte bg = (byte)Math.Clamp((int)Math.Round(g), 0, 255);
            byte bb = (byte)Math.Clamp((int)Math.Round(b), 0, 255);

            return (br, bg, bb);
        }

        /// <summary>
        /// High-speed Q10 fixed-point conversion using scaled integer arithmetic.
        /// Zero heap allocation.
        /// </summary>
        public static (byte r, byte g, byte b) ConvertPixelQ10(byte y, byte u, byte v)
        {
            int dU = u - 128;
            int dV = v - 128;

            int y10 = y << 10;
            int r10 = y10 + 1440 * dU;
            int g10 = y10 - 354 * dV - 734 * dU;
            int b10 = y10 + 1822 * dV;

            return (Clamp(r10 >> 10), Clamp(g10 >> 10), Clamp(b10 >> 10));
        }

        /// <summary>
        /// Converts YUV422 packed frame to standard RGB24 format.
        /// </summary>
        public static byte[] ConvertYuv422ToRgb24(ReadOnlySpan<byte> yuv, int width, int height)
        {
            int pixelCount = width * height;
            byte[] rgb = new byte[pixelCount * 3];
            ConvertYuv422ToRgb24(yuv, rgb.AsSpan(), width, height);
            return rgb;
        }

        /// <summary>
        /// Converts YUV422 packed frame to standard RGB24 format with zero heap allocation into provided destination buffer.
        /// </summary>
        public static void ConvertYuv422ToRgb24(ReadOnlySpan<byte> yuv, Span<byte> rgb, int width, int height)
        {
            int yuvIdx = 0;
            int rgbIdx = 0;

            while (yuvIdx <= yuv.Length - 4 && rgbIdx <= rgb.Length - 6)
            {
                byte u = yuv[yuvIdx];
                byte y0 = yuv[yuvIdx + 1];
                byte v = yuv[yuvIdx + 2];
                byte y1 = yuv[yuvIdx + 3];

                int dU = u - 128;
                int dV = v - 128;

                int deltaR = (1440 * dU) >> 10;
                int deltaG = (-354 * dV - 734 * dU) >> 10;
                int deltaB = (1822 * dV) >> 10;

                // Pixel 0 (RGB)
                rgb[rgbIdx] = Clamp(y0 + deltaR);
                rgb[rgbIdx + 1] = Clamp(y0 + deltaG);
                rgb[rgbIdx + 2] = Clamp(y0 + deltaB);

                // Pixel 1 (RGB)
                rgb[rgbIdx + 3] = Clamp(y1 + deltaR);
                rgb[rgbIdx + 4] = Clamp(y1 + deltaG);
                rgb[rgbIdx + 5] = Clamp(y1 + deltaB);

                yuvIdx += 4;
                rgbIdx += 6;
            }
        }

        /// <summary>
        /// Converts YUV422 packed frame to Windows native BGR24 format.
        /// </summary>
        public static byte[] ConvertYuv422ToBgr24(ReadOnlySpan<byte> yuv, int width, int height)
        {
            int pixelCount = width * height;
            byte[] bgr = new byte[pixelCount * 3];
            ConvertYuv422ToBgr24(yuv, bgr.AsSpan(), width, height);
            return bgr;
        }

        /// <summary>
        /// Converts YUV422 packed frame to Windows native BGR24 format with zero heap allocation into provided destination buffer.
        /// </summary>
        public static void ConvertYuv422ToBgr24(ReadOnlySpan<byte> yuv, Span<byte> bgr, int width, int height)
        {
            int yuvIdx = 0;
            int bgrIdx = 0;

            while (yuvIdx <= yuv.Length - 4 && bgrIdx <= bgr.Length - 6)
            {
                byte u = yuv[yuvIdx];
                byte y0 = yuv[yuvIdx + 1];
                byte v = yuv[yuvIdx + 2];
                byte y1 = yuv[yuvIdx + 3];

                int dU = u - 128;
                int dV = v - 128;

                int deltaR = (1440 * dU) >> 10;
                int deltaG = (-354 * dV - 734 * dU) >> 10;
                int deltaB = (1822 * dV) >> 10;

                // Pixel 0 (BGR)
                bgr[bgrIdx] = Clamp(y0 + deltaB);
                bgr[bgrIdx + 1] = Clamp(y0 + deltaG);
                bgr[bgrIdx + 2] = Clamp(y0 + deltaR);

                // Pixel 1 (BGR)
                bgr[bgrIdx + 3] = Clamp(y1 + deltaB);
                bgr[bgrIdx + 4] = Clamp(y1 + deltaG);
                bgr[bgrIdx + 5] = Clamp(y1 + deltaR);

                yuvIdx += 4;
                bgrIdx += 6;
            }
        }

        /// <summary>
        /// Unsafe direct scanline blit from YUV422 pointer into BGR24 pointer.
        /// Zero GC allocations, branchless LUT indexing.
        /// </summary>
        public static unsafe void ConvertYuv422ToBgr24(byte* pYuv, byte* pBgr, int pixelCount)
        {
            fixed (byte* pClamp = &ClampTable[384])
            {
                int quadCount = pixelCount >> 1;
                for (int i = 0; i < quadCount; i++)
                {
                    int u = pYuv[0] - 128;
                    int y0 = pYuv[1];
                    int v = pYuv[2] - 128;
                    int y1 = pYuv[3];
                    pYuv += 4;

                    int deltaR = (1440 * u) >> 10;
                    int deltaG = (-354 * v - 734 * u) >> 10;
                    int deltaB = (1822 * v) >> 10;

                    // Pixel 0 (BGR)
                    pBgr[0] = pClamp[y0 + deltaB];
                    pBgr[1] = pClamp[y0 + deltaG];
                    pBgr[2] = pClamp[y0 + deltaR];

                    // Pixel 1 (BGR)
                    pBgr[3] = pClamp[y1 + deltaB];
                    pBgr[4] = pClamp[y1 + deltaG];
                    pBgr[5] = pClamp[y1 + deltaR];

                    pBgr += 6;
                }
            }
        }

        /// <summary>
        /// Rasterizes a full YUV422 frame directly into an unmanaged BGR24 bitmap backbuffer taking stride into account.
        /// </summary>
        public static unsafe void ConvertYuv422ToBgr24Stride(byte* pYuv, byte* pBackBuffer, int width, int height, int stride)
        {
            int yuvLineBytes = width * 2;
            for (int line = 0; line < height; line++)
            {
                byte* pSrcLine = pYuv + (line * yuvLineBytes);
                byte* pDstLine = pBackBuffer + (line * stride);
                ConvertYuv422ToBgr24(pSrcLine, pDstLine, width);
            }
        }
    }
}
