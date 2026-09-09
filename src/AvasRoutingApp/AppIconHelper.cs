using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvasRoutingApp.Logging;

namespace AvasRoutingApp
{
    /// <summary>
    /// Utility for dynamically loading the application icon (logo.ico).
    /// Supports user replacement of logo.ico next to the .exe without recompiling,
    /// and uses non-locking read semantics so the file can be modified while running.
    /// </summary>
    public static class AppIconHelper
    {
        private const string LogoFileName = "logo.ico";

        public static string IconFilePath => Path.Combine(AppContext.BaseDirectory, LogoFileName);

        /// <summary>
        /// Retrieves an ImageSource for UI displays (e.g. Header bar or About dialog),
        /// selecting the frame closest to the preferred pixel size.
        /// </summary>
        public static ImageSource? GetAppIcon(int preferredSize = 32)
        {
            try
            {
                string path = IconFilePath;
                if (!File.Exists(path))
                {
                    return null;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                var bestFrame = decoder.Frames
                    .OrderBy(f => Math.Abs(f.PixelWidth - preferredSize))
                    .FirstOrDefault() ?? decoder.Frames.FirstOrDefault();

                if (bestFrame != null)
                {
                    bestFrame.Freeze();
                    return bestFrame;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("AppIcon", $"Could not load custom icon from {IconFilePath}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Retrieves an ImageSource for Window.Icon, selecting the highest-resolution frame available.
        /// </summary>
        public static ImageSource? GetWindowIcon()
        {
            try
            {
                string path = IconFilePath;
                if (!File.Exists(path))
                {
                    return null;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                var bestFrame = decoder.Frames
                    .OrderByDescending(f => f.PixelWidth)
                    .FirstOrDefault();

                if (bestFrame != null)
                {
                    bestFrame.Freeze();
                    return bestFrame;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("AppIcon", $"Could not load window icon from {IconFilePath}: {ex.Message}");
            }

            return null;
        }
    }
}
