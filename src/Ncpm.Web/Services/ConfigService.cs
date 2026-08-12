using System.Text;
using Ncpm.Data;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ncpm.Services;

public class ConfigService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigService> _logger;
    private readonly string _configPath;
    private readonly string _proxyHostsPath;
    private readonly string _certificatesPath;
    private readonly string _secretsPath;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    // LoadAppConfig sits on hot paths (config generation, ACL middleware, auth).
    // Cache the parsed result and invalidate on write or on-disk change.
    private readonly object _appConfigLock = new();
    private AppConfig? _appConfigCache;

    public event Action? OnConfigChanged;

    /// <summary>Root directory holding all user configuration.</summary>
    public string ConfigPath => _configPath;

    /// <summary>
    /// Directory for sensitive runtime state (sessions, ACME account keys).
    /// Kept outside <see cref="ConfigPath"/> so writes do not trip the config watcher.
    /// </summary>
    public string SecretsPath => _secretsPath;

    public ConfigService(IConfiguration configuration, ILogger<ConfigService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _configPath = configuration.GetValue<string>("Config:Path") ?? "data/config";
        _proxyHostsPath = Path.Combine(_configPath, "proxy-hosts");
        _certificatesPath = Path.Combine(_configPath, "certificates");
        _secretsPath = configuration.GetValue<string>("Config:SecretsPath")
            ?? Path.Combine(Path.GetDirectoryName(_configPath.TrimEnd('/', '\\')) ?? "data", "secrets");

        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        // Tolerate unknown keys so configs written by a newer or older build still load
        // instead of failing the whole file.
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        EnsureDirectoriesExist();
        SetupFileWatcher();
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_configPath);
        Directory.CreateDirectory(_proxyHostsPath);
        Directory.CreateDirectory(_certificatesPath);
        Directory.CreateDirectory(_secretsPath);
    }

    private void SetupFileWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(_configPath)
            {
                Filter = "*.yml",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnWatcherEvent;
            _watcher.Created += OnWatcherEvent;
            _watcher.Deleted += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to setup file watcher");
        }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        InvalidateAppConfigCache();
        OnConfigChanged?.Invoke();
    }

    /// <summary>Drops the cached <see cref="AppConfig"/> so the next read hits disk.</summary>
    public void InvalidateAppConfigCache()
    {
        lock (_appConfigLock)
        {
            _appConfigCache = null;
        }
    }

    public AppConfig LoadAppConfig()
    {
        lock (_appConfigLock)
        {
            if (_appConfigCache != null)
                return _appConfigCache;
        }

        var config = ReadAppConfigFromDisk();

        lock (_appConfigLock)
        {
            _appConfigCache = config;
        }

        return config;
    }

    /// <summary>
    /// Builds the config.yml written on first run. appsettings.json / environment
    /// variables seed the initial values; after that the YAML file is authoritative.
    /// </summary>
    private AppConfig BuildSeedConfig()
    {
        var config = new AppConfig();

        _configuration.GetSection("Docker").Bind(config.Docker);
        _configuration.GetSection("Nginx").Bind(config.Nginx);
        _configuration.GetSection("Security").Bind(config.Security);
        _configuration.GetSection("Panel").Bind(config.Panel);

        return config;
    }

    private AppConfig ReadAppConfigFromDisk()
    {
        var filePath = Path.Combine(_configPath, "config.yml");
        if (!File.Exists(filePath))
        {
            var defaultConfig = BuildSeedConfig();
            SaveAppConfig(defaultConfig);
            _logger.LogInformation("Created default config at {Path}", filePath);
            return defaultConfig;
        }

        try
        {
            var content = File.ReadAllText(filePath);
            return _deserializer.Deserialize<AppConfig>(content) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load app config");
            return new AppConfig();
        }
    }

    public void SaveAppConfig(AppConfig config)
    {
        var filePath = Path.Combine(_configPath, "config.yml");
        try
        {
            var content = _serializer.Serialize(config);
            File.WriteAllText(filePath, content);

            lock (_appConfigLock)
            {
                _appConfigCache = config;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save app config");
            throw;
        }
    }

    public List<ProxyHostConfig> LoadProxyHosts()
    {
        var hosts = new List<ProxyHostConfig>();
        if (!Directory.Exists(_proxyHostsPath))
            return hosts;

        foreach (var file in Directory.GetFiles(_proxyHostsPath, "*.yml"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var host = _deserializer.Deserialize<ProxyHostConfig>(content);
                hosts.Add(host);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load proxy host from {File}", file);
            }
        }

        return hosts;
    }

    /// <summary>
    /// Loads discovered hosts from the separate discovered-hosts directory so
    /// callers can merge them with manual hosts for a single UI list.
    /// </summary>
    public List<ProxyHostConfig> LoadDiscoveredProxyHosts()
    {
        var hosts = new List<ProxyHostConfig>();
        var dir = Path.Combine(_configPath, "discovered-hosts");
        if (!Directory.Exists(dir))
            return hosts;

        foreach (var file in Directory.GetFiles(dir, "*.yml"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var host = _deserializer.Deserialize<ProxyHostConfig>(content);
                if (host != null)
                    hosts.Add(host);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load discovered host from {File}", file);
            }
        }

        return hosts;
    }

    /// <summary>Exposes the YAML serializer for callers writing hosts outside the manual directory.</summary>
    public string SerializeProxyHost(ProxyHostConfig host) => _serializer.Serialize(host);

    /// <summary>Exposes the YAML deserializer for callers reading host files directly.</summary>
    public ProxyHostConfig? DeserializeProxyHost(string content)
        => string.IsNullOrEmpty(content) ? null : _deserializer.Deserialize<ProxyHostConfig>(content);

    public ProxyHostConfig? LoadProxyHost(string id)
    {
        var filePath = Path.Combine(_proxyHostsPath, $"{id}.yml");
        if (!File.Exists(filePath))
            return null;

        try
        {
            var content = File.ReadAllText(filePath);
            return _deserializer.Deserialize<ProxyHostConfig>(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load proxy host {Id}", id);
            return null;
        }
    }

    public void SaveProxyHost(ProxyHostConfig host)
    {
        var filePath = Path.Combine(_proxyHostsPath, $"{host.Id}.yml");
        try
        {
            host.UpdatedAt = DateTime.UtcNow;
            var content = _serializer.Serialize(host);
            File.WriteAllText(filePath, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save proxy host {Id}", host.Id);
            throw;
        }
    }

    public bool DeleteProxyHost(string id)
    {
        var filePath = Path.Combine(_proxyHostsPath, $"{id}.yml");
        if (!File.Exists(filePath))
            return false;

        try
        {
            File.Delete(filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete proxy host {Id}", id);
            return false;
        }
    }

    public List<CertificateConfig> LoadCertificates()
    {
        var certs = new List<CertificateConfig>();
        var filePath = Path.Combine(_certificatesPath, "certificates.yml");
        if (!File.Exists(filePath))
            return certs;

        try
        {
            var content = File.ReadAllText(filePath);
            return _deserializer.Deserialize<List<CertificateConfig>>(content) ?? certs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load certificates");
            return certs;
        }
    }

    public void SaveCertificates(List<CertificateConfig> certificates)
    {
        var filePath = Path.Combine(_certificatesPath, "certificates.yml");
        try
        {
            var content = _serializer.Serialize(certificates);
            File.WriteAllText(filePath, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save certificates");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnWatcherEvent;
            _watcher.Created -= OnWatcherEvent;
            _watcher.Deleted -= OnWatcherEvent;
            _watcher.Renamed -= OnWatcherEvent;
            _watcher.Dispose();
            _watcher = null;
        }

        _disposed = true;
    }
}
