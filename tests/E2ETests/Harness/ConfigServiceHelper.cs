using System;
using System.IO;
using System.Text.Json;

namespace E2ETests.Harness
{
    public interface IConfigService
    {
        AppConfig Current { get; }
        void Save(AppConfig config);
        event Action<AppConfig>? ConfigChanged;
    }

    public class ConfigServiceHelper : IConfigService
    {
        private readonly string _configFilePath;
        private readonly object _lock = new();
        private AppConfig _current;

        public AppConfig Current
        {
            get
            {
                lock (_lock) return _current;
            }
        }

        public event Action<AppConfig>? ConfigChanged;

        public ConfigServiceHelper(string configFilePath)
        {
            _configFilePath = configFilePath;
            _current = LoadOrCreate();
        }

        private AppConfig LoadOrCreate()
        {
            lock (_lock)
            {
                if (File.Exists(_configFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_configFilePath);
                        var loaded = JsonSerializer.Deserialize<AppConfig>(json);
                        if (loaded != null && loaded.Validate(out _))
                        {
                            return loaded;
                        }
                    }
                    catch
                    {
                        // Corruption recovery: return default configuration
                    }
                }

                var def = new AppConfig();
                SaveInternal(def);
                return def;
            }
        }

        public void Save(AppConfig config)
        {
            if (!config.Validate(out string? err))
            {
                throw new ArgumentException($"Invalid configuration: {err}");
            }

            lock (_lock)
            {
                SaveInternal(config);
                _current = config;
            }

            ConfigChanged?.Invoke(_current);
        }

        private void SaveInternal(AppConfig config)
        {
            string dir = Path.GetDirectoryName(_configFilePath)!;
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            string tempFile = _configFilePath + ".tmp";

            // Atomic file write using temporary file and replacement
            File.WriteAllText(tempFile, json);
            if (File.Exists(_configFilePath))
            {
                File.Replace(tempFile, _configFilePath, null);
            }
            else
            {
                File.Move(tempFile, _configFilePath);
            }
        }
    }
}
