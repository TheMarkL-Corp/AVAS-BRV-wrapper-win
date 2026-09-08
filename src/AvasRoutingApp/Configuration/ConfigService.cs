using System;
using System.IO;
using System.Text.Json;

namespace AvasRoutingApp.Configuration
{
    /// <summary>
    /// Service contract for application configuration management.
    /// Strictly conforms to PROJECT.md § Interface Contracts.
    /// </summary>
    public interface IConfigService
    {
        AppConfig Current { get; }
        void Save(AppConfig config);
        event Action<AppConfig>? ConfigChanged;
    }

    /// <summary>
    /// Handles atomic JSON persistence and thread-safe caching of AppConfig.
    /// Uses write-and-replace semantics (.tmp file) to prevent configuration corruption.
    /// </summary>
    public class ConfigService : IConfigService
    {
        private readonly string _filePath;
        private readonly object _lock = new();
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private AppConfig _current;

        public event Action<AppConfig>? ConfigChanged;

        public AppConfig Current
        {
            get
            {
                lock (_lock)
                {
                    return _current.Clone();
                }
            }
        }

        public string FilePath => _filePath;

        public ConfigService(string? filePath = null)
        {
            _filePath = filePath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            _current = LoadOrCreateDefault();
        }

        public void Save(AppConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config), "Configuration to save cannot be null.");
            }

            lock (_lock)
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmpFile = _filePath + ".tmp." + Guid.NewGuid().ToString("N");
                string json = JsonSerializer.Serialize(config, _jsonOptions);

                File.WriteAllText(tmpFile, json);

                // Atomic move / replace
                File.Move(tmpFile, _filePath, overwrite: true);

                _current = config.Clone();
            }

            // Raise change event outside lock
            ConfigChanged?.Invoke(_current.Clone());
        }

        public void Reload()
        {
            lock (_lock)
            {
                _current = LoadOrCreateDefault();
            }
            ConfigChanged?.Invoke(_current.Clone());
        }

        private AppConfig LoadOrCreateDefault()
        {
            if (!File.Exists(_filePath))
            {
                var defaultConfig = new AppConfig();
                try
                {
                    string? dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string tmpFile = _filePath + ".tmp." + Guid.NewGuid().ToString("N");
                    string json = JsonSerializer.Serialize(defaultConfig, _jsonOptions);
                    File.WriteAllText(tmpFile, json);
                    File.Move(tmpFile, _filePath, overwrite: true);
                }
                catch
                {
                    // If directory is read-only or error writing initial file, continue with in-memory default
                }
                return defaultConfig;
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions);
                return loaded ?? new AppConfig();
            }
            catch
            {
                // Fallback to default on corrupted JSON
                return new AppConfig();
            }
        }
    }
}
