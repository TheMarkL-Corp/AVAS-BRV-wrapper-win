using System;

namespace E2ETests.Harness
{
    public static class Yuv422Rasterizer
    {
        private static readonly byte[] ClampLut = new byte[2048]; // indexed from -1024 to +1023 (+1024 offset)

        static Yuv422Rasterizer()
        {
            for (int i = -1024; i < 1024; i++)
            {
                ClampLut[i + 1024] = (byte)Math.Clamp(i, 0, 255);
            }
        }

        public static byte Clamp(int val)
        {
            if (val < -1024) return 0;
            if (val >= 1024) return 255;
            return ClampLut[val + 1024];
        }

        /// <summary>
        /// Reference floating-point conversion according to ORIGINAL_REQUEST.md formula:
        /// R = Y + 1.4065*(U - 128)
        /// G = Y - 0.3455*(V - 128) - 0.7169*(U - 128)
        /// B = Y + 1.7790*(V - 128)
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
        /// Q10 fixed-point conversion for high-performance zero-allocation rasterization.
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

        public static byte[] ConvertYuv422ToRgb24(ReadOnlySpan<byte> yuv, int width, int height)
        {
            int pixelCount = width * height;
            byte[] rgb = new byte[pixelCount * 3];

            int yuvIdx = 0;
            int rgbIdx = 0;

            while (yuvIdx < yuv.Length - 3 && rgbIdx < rgb.Length - 5)
            {
                byte u = yuv[yuvIdx];
                byte y0 = yuv[yuvIdx + 1];
                byte v = yuv[yuvIdx + 2];
                byte y1 = yuv[yuvIdx + 3];

                var p0 = ConvertPixelQ10(y0, u, v);
                var p1 = ConvertPixelQ10(y1, u, v);

                rgb[rgbIdx] = p0.r;
                rgb[rgbIdx + 1] = p0.g;
                rgb[rgbIdx + 2] = p0.b;

                rgb[rgbIdx + 3] = p1.r;
                rgb[rgbIdx + 4] = p1.g;
                rgb[rgbIdx + 5] = p1.b;

                yuvIdx += 4;
                rgbIdx += 6;
            }

            return rgb;
        }

        public static byte[] ConvertYuv422ToBgr24(ReadOnlySpan<byte> yuv, int width, int height)
        {
            int pixelCount = width * height;
            byte[] bgr = new byte[pixelCount * 3];

            int yuvIdx = 0;
            int bgrIdx = 0;

            while (yuvIdx < yuv.Length - 3 && bgrIdx < bgr.Length - 5)
            {
                byte u = yuv[yuvIdx];
                byte y0 = yuv[yuvIdx + 1];
                byte v = yuv[yuvIdx + 2];
                byte y1 = yuv[yuvIdx + 3];

                var p0 = ConvertPixelQ10(y0, u, v);
                var p1 = ConvertPixelQ10(y1, u, v);

                bgr[bgrIdx] = p0.b;
                bgr[bgrIdx + 1] = p0.g;
                bgr[bgrIdx + 2] = p0.r;

                bgr[bgrIdx + 3] = p1.b;
                bgr[bgrIdx + 4] = p1.g;
                bgr[bgrIdx + 5] = p1.r;

                yuvIdx += 4;
                bgrIdx += 6;
            }

            return bgr;
        }
    }
}
