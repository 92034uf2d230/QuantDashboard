using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDashboard.Models;

namespace QuantDashboard.Managers
{
    /// <summary>
    /// Singleton manager for application settings
    /// Handles loading, saving, and runtime modification of settings
    /// </summary>
    public sealed class SettingsManager
    {
        private static readonly Lazy<SettingsManager> _lazy =
            new Lazy<SettingsManager>(() => new SettingsManager());

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static SettingsManager Instance => _lazy.Value;

        private readonly string _settingsPath;
        private AppSettings _currentSettings;
        private FileSystemWatcher? _watcher;

        /// <summary>
        /// Current application settings
        /// </summary>
        public AppSettings CurrentSettings => _currentSettings;

        /// <summary>
        /// Event fired when settings are reloaded
        /// </summary>
        public event Action<AppSettings>? OnSettingsReloaded;

        private SettingsManager()
        {
            // Determine settings path relative to executable location
            var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            _settingsPath = Path.Combine(baseDir, "settings.json");
            _currentSettings = AppSettings.CreateDefault();
        }

        /// <summary>
        /// Load settings from file. Creates default settings file if not exists.
        /// Supports environment variable override via QUANT_MODE.
        /// </summary>
        public AppSettings Load()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    Console.WriteLine($"[SettingsManager] Settings file not found at: {_settingsPath}");
                    Console.WriteLine("[SettingsManager] Creating default settings file...");
                    _currentSettings = AppSettings.CreateDefault();
                    Save(_currentSettings);
                }
                else
                {
                    var json = File.ReadAllText(_settingsPath);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        WriteIndented = true
                    };

                    _currentSettings = JsonSerializer.Deserialize<AppSettings>(json, options)
                                       ?? AppSettings.CreateDefault();

                    // Validate settings
                    if (!_currentSettings.Validate(out var errorMessage))
                    {
                        Console.WriteLine($"[SettingsManager] Invalid settings: {errorMessage}");
                        Console.WriteLine("[SettingsManager] Using default values for invalid fields.");
                    }

                    Console.WriteLine($"[SettingsManager] Loaded settings from: {_settingsPath}");
                }

                // Apply environment variable override for Mode
                var envMode = Environment.GetEnvironmentVariable("QUANT_MODE");
                if (!string.IsNullOrEmpty(envMode))
                {
                    Console.WriteLine($"[SettingsManager] Environment override: QUANT_MODE={envMode}");
                    _currentSettings.Mode = envMode;
                }

                Console.WriteLine($"[SettingsManager] Current Mode: {_currentSettings.Mode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsManager] Error loading settings: {ex.Message}");
                Console.WriteLine("[SettingsManager] Using default settings.");
                _currentSettings = AppSettings.CreateDefault();
            }

            return _currentSettings;
        }

        /// <summary>
        /// Save settings to file
        /// </summary>
        public void Save(AppSettings settings)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    WriteIndented = true
                };

                var json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(_settingsPath, json);
                _currentSettings = settings;
                Console.WriteLine($"[SettingsManager] Settings saved to: {_settingsPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsManager] Error saving settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Enable file watcher to detect runtime changes
        /// </summary>
        public void EnableFileWatcher()
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                var fileName = Path.GetFileName(_settingsPath);

                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    Console.WriteLine("[SettingsManager] Cannot enable file watcher: directory does not exist.");
                    return;
                }

                _watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
                };

                _watcher.Changed += OnFileChanged;
                _watcher.EnableRaisingEvents = true;

                Console.WriteLine("[SettingsManager] File watcher enabled for settings.json");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsManager] Error enabling file watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Disable file watcher
        /// </summary>
        public void DisableFileWatcher()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileChanged;
                _watcher.Dispose();
                _watcher = null;
                Console.WriteLine("[SettingsManager] File watcher disabled");
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Add small delay to ensure file write is complete
                System.Threading.Thread.Sleep(100);

                Console.WriteLine("[SettingsManager] Settings file changed, reloading...");
                var previousMode = _currentSettings.Mode;
                Load();

                if (previousMode != _currentSettings.Mode)
                {
                    Console.WriteLine($"[SettingsManager] Mode changed from {previousMode} to {_currentSettings.Mode}");
                    Console.WriteLine("[SettingsManager] Note: Mode changes require application restart to take effect.");
                }

                OnSettingsReloaded?.Invoke(_currentSettings);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsManager] Error reloading settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Update settings at runtime
        /// </summary>
        public void UpdateSettings(Action<AppSettings> updateAction)
        {
            updateAction(_currentSettings);

            if (!_currentSettings.Validate(out var errorMessage))
            {
                Console.WriteLine($"[SettingsManager] Invalid settings update: {errorMessage}");
                return;
            }

            Save(_currentSettings);
            OnSettingsReloaded?.Invoke(_currentSettings);
        }

        /// <summary>
        /// Get the path to the settings file
        /// </summary>
        public string SettingsFilePath => _settingsPath;
    }
}
