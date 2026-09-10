using System;
using System.Reflection;

namespace AvasRoutingApp
{
    /// <summary>
    /// Centralized application metadata and version information.
    /// Ensures UI, logs, and dialogs consistently display the exact build version.
    /// </summary>
    public static class AppInfo
    {
        public const string AppName = "AVAS Routing Software";
        public const string AppSubtitle = "Dual Link SDVoE Manager";

        /// <summary>
        /// Formatted version string, e.g. "v1.1.0".
        /// Dynamically sourced from assembly metadata.
        /// </summary>
        public static string VersionString { get; } = InitializeVersion();

        private static string InitializeVersion()
        {
            try
            {
                var asm = typeof(AppInfo).Assembly;
                var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(infoVer))
                {
                    // Strip any commit hash metadata appended by dotnet build
                    int plusIdx = infoVer.IndexOf('+');
                    string rawVer = plusIdx >= 0 ? infoVer.Substring(0, plusIdx) : infoVer;
                    return rawVer.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? rawVer : $"v{rawVer}";
                }

                var ver = asm.GetName().Version;
                if (ver != null)
                {
                    return $"v{ver.Major}.{ver.Minor}.{ver.Build}";
                }
            }
            catch
            {
                // Fallback safe default
            }

            return "v1.2.0";
        }
    }
}
