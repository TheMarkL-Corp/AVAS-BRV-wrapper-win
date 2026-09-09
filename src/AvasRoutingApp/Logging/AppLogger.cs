using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AvasRoutingApp.Logging
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
        Fatal
    }

    /// <summary>
    /// Lightweight, thread-safe, zero-dependency file and trace logger for AVAS Routing Software.
    /// Writes timestamped log entries to app.log in the application directory.
    /// </summary>
    public static class AppLogger
    {
        private static readonly object LockObj = new();
        private static string _logFilePath = string.Empty;
        private static bool _initialized;

        public static string LogFilePath
        {
            get
            {
                if (!_initialized)
                {
                    Initialize();
                }
                return _logFilePath;
            }
        }

        public static void Initialize(string? customLogPath = null)
        {
            lock (LockObj)
            {
                if (_initialized && string.IsNullOrEmpty(customLogPath)) return;

                try
                {
                    if (!string.IsNullOrWhiteSpace(customLogPath))
                    {
                        _logFilePath = customLogPath;
                    }
                    else
                    {
                        string baseDir = AppContext.BaseDirectory;
                        _logFilePath = Path.Combine(baseDir, "app.log");

                        // Test write access; fallback to local appdata if base directory is protected
                        try
                        {
                            using var fs = File.Open(_logFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                        }
                        catch
                        {
                            string appData = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "AvasRoutingApp",
                                "Logs");
                            Directory.CreateDirectory(appData);
                            _logFilePath = Path.Combine(appData, "app.log");
                        }
                    }

                    _initialized = true;
                    WriteInternal(LogLevel.Info, "AppLogger", $"=== Application Logging Initialized | Log file: {_logFilePath} ===");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[AppLogger] Failed to initialize file logger: {ex.Message}");
                }
            }
        }

        public static void Debug(string category, string message) => Log(LogLevel.Debug, category, message);
        public static void Info(string category, string message) => Log(LogLevel.Info, category, message);
        public static void Warn(string category, string message, Exception? ex = null) => Log(LogLevel.Warn, category, message, ex);
        public static void Error(string category, string message, Exception? ex = null) => Log(LogLevel.Error, category, message, ex);
        public static void Fatal(string category, string message, Exception? ex = null) => Log(LogLevel.Fatal, category, message, ex);

        public static void Log(LogLevel level, string category, string message, Exception? ex = null)
        {
            if (!_initialized)
            {
                Initialize();
            }

            string fullMessage = ex != null
                ? $"{message} | Exception: {ex.GetType().FullName}: {ex.Message}\nStackTrace:\n{ex.StackTrace}"
                : message;

            WriteInternal(level, category, fullMessage);
        }

        private static void WriteInternal(LogLevel level, string category, string message)
        {
            string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string levelStr = level.ToString().ToUpperInvariant().PadRight(5);
            int threadId = Thread.CurrentThread.ManagedThreadId;
            string formattedLine = $"[{timeStr}] [{levelStr}] [T{threadId:D2}] [{category}] {message}";

            Trace.WriteLine(formattedLine);

            if (string.IsNullOrEmpty(_logFilePath)) return;

            lock (LockObj)
            {
                try
                {
                    File.AppendAllText(_logFilePath, formattedLine + Environment.NewLine);
                }
                catch
                {
                    // Fail silently on logging write error so logger never causes crash
                }
            }
        }
    }
}
