using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Ncpm.Data;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ncpm.Services;

public class ConfigService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigService> _logger;
    private readonly IDataProtector _secretProtector;
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

    /// <summary>Root directory for all mutable panel data.</summary>
    public string DataPath =>
        Path.GetDirectoryName(Path.GetFullPath(_configPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        ?? Path.GetFullPath("data");

    /// <summary>
    /// Directory for sensitive runtime state (sessions, ACME account keys).
    /// Kept outside <see cref="ConfigPath"/> so writes do not trip the config watcher.
    /// </summary>
    public string SecretsPath => _secretsPath;

    public ConfigService(
        IConfiguration configuration,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<ConfigService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _secretProtector = dataProtectionProvider.CreateProtector("Ncpm.AppConfigSecrets.v1");
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
        RaiseConfigChanged();
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
        _configuration.GetSection("Acme").Bind(config.Acme);
        _configuration.GetSection("Compose").Bind(config.Compose);
        _configuration.GetSection("Logging").Bind(config.Logging);
        _configuration.GetSection("Security").Bind(config.Security);
        _configuration.GetSection("Panel").Bind(config.Panel);
        _configuration.GetSection("Acl").Bind(config.Acl);
        _configuration.GetSection("RateLimit").Bind(config.RateLimit);
        _configuration.GetSection("Notification").Bind(config.Notification);

        if (_configuration.GetSection("Oidc").Exists())
        {
            config.Oidc = new OidcConfig();
            _configuration.GetSection("Oidc").Bind(config.Oidc);
        }

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
            var config = _deserializer.Deserialize<AppConfig>(content) ?? new AppConfig();
            var containsLegacyPlaintextSecrets = RestoreProtectedSecrets(config);
            ValidateAppConfig(config);
            if (containsLegacyPlaintextSecrets)
            {
                SaveAppConfig(config);
                _logger.LogInformation("Encrypted legacy plaintext application secrets in {Path}", filePath);
            }
            return config;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to load application config from {Path}", filePath);
            throw new InvalidOperationException(
                $"Application config '{filePath}' is invalid or unreadable. The file was preserved for manual recovery.",
                ex);
        }
    }

    public void SaveAppConfig(AppConfig config)
    {
        ValidateAppConfig(config);
        var filePath = Path.Combine(_configPath, "config.yml");
        try
        {
            var persistedConfig = CloneAppConfig(config);
            ProtectSecrets(persistedConfig);
            var content = _serializer.Serialize(persistedConfig);
            WriteFileAtomically(filePath, content, restrictToOwner: true);

            lock (_appConfigLock)
            {
                _appConfigCache = config;
            }

            RaiseConfigChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save app config");
            throw;
        }
    }

    private void RaiseConfigChanged()
    {
        var handlers = OnConfigChanged?.GetInvocationList();
        if (handlers == null)
            return;

        foreach (var handler in handlers.Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A configuration change subscriber failed");
            }
        }
    }

    /// <summary>Serializes an application config using the same schema as config.yml.</summary>
    public string SerializeAppConfig(AppConfig config) => _serializer.Serialize(config);

    private AppConfig CloneAppConfig(AppConfig config) =>
        _deserializer.Deserialize<AppConfig>(_serializer.Serialize(config)) ?? new AppConfig();

    private void ProtectSecrets(AppConfig config)
    {
        foreach (var provider in config.Notification.Providers)
        {
            if (!string.IsNullOrWhiteSpace(provider.Token)
                && !provider.Token.StartsWith("enc:v1:", StringComparison.Ordinal))
            {
                provider.Token = "enc:v1:" + _secretProtector.Protect(provider.Token);
            }
        }

        if (!string.IsNullOrWhiteSpace(config.Oidc?.ClientSecret)
            && !config.Oidc.ClientSecret.StartsWith("enc:v1:", StringComparison.Ordinal))
        {
            config.Oidc.ClientSecret = "enc:v1:" + _secretProtector.Protect(config.Oidc.ClientSecret);
        }
    }

    private bool RestoreProtectedSecrets(AppConfig config)
    {
        var legacyPlaintext = false;
        foreach (var provider in config.Notification.Providers)
        {
            provider.Token = RestoreSecret(provider.Token, ref legacyPlaintext);
        }

        if (config.Oidc != null)
            config.Oidc.ClientSecret = RestoreSecret(config.Oidc.ClientSecret, ref legacyPlaintext) ?? string.Empty;

        return legacyPlaintext;
    }

    private string? RestoreSecret(string? value, ref bool legacyPlaintext)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        if (value.StartsWith("enc:v1:", StringComparison.Ordinal))
            return _secretProtector.Unprotect(value["enc:v1:".Length..]);

        legacyPlaintext = true;
        return value;
    }

    private static void WriteFileAtomically(string path, string content, bool restrictToOwner)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content);
            if (restrictToOwner)
                RestrictFileToOwner(temp);
            File.Move(temp, path, overwrite: true);
            if (restrictToOwner)
                RestrictFileToOwner(path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void RestrictFileToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Some bind-mounted filesystems do not expose Unix permission changes.
        }
    }

    /// <summary>
    /// Parses and validates YAML entered in the raw configuration editor. This is
    /// intentionally separate from saving so malformed YAML never replaces the
    /// last working config.yml.
    /// </summary>
    public AppConfig ParseAppConfig(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            throw new InvalidOperationException("YAML 配置不能为空");

        AppConfig config;
        try
        {
            config = _deserializer.Deserialize<AppConfig>(yaml)
                ?? throw new InvalidOperationException("YAML 中没有可用配置");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidOperationException($"YAML 格式错误（第 {ex.Start.Line} 行，第 {ex.Start.Column} 列）：{ex.Message}", ex);
        }

        ValidateAppConfig(config);
        return config;
    }

    public static void ValidateAppConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Panel == null || config.Docker == null || config.Nginx == null
            || config.Acme == null || config.Compose == null || config.Logging == null
            || config.Security == null || config.Acl == null || config.RateLimit == null
            || config.Notification == null)
        {
            throw new InvalidOperationException("配置缺少必需的顶级节点");
        }

        if (string.IsNullOrWhiteSpace(config.Panel.Host))
            throw new InvalidOperationException("Panel.Host 不能为空");
        if (config.Panel.Port is < 1 or > 65535)
            throw new InvalidOperationException("Panel.Port 必须在 1-65535 之间");

        config.Panel.BasePath = NormalizeBasePath(config.Panel.BasePath);
        config.Panel.TrustedProxies ??= new List<string>();
        if (config.Panel.ForwardedHeaderLimit is < 1 or > 10)
            throw new InvalidOperationException("Panel.ForwardedHeaderLimit 必须在 1-10 之间");
        foreach (var proxy in config.Panel.TrustedProxies)
        {
            var value = proxy.Trim();
            var valid = value.Contains('/')
                ? System.Net.IPNetwork.TryParse(value, out _)
                : System.Net.IPAddress.TryParse(value, out _);
            if (!valid)
                throw new InvalidOperationException($"Panel.TrustedProxies 包含无效 IP 或 CIDR：{proxy}");
        }

        if (string.IsNullOrWhiteSpace(config.Docker.Host))
            throw new InvalidOperationException("Docker.Host 不能为空");
        if (config.Docker.ApiTimeout <= 0)
            throw new InvalidOperationException("Docker.ApiTimeout 必须大于 0 秒");
        if (config.Compose.CommandTimeout <= 0)
            throw new InvalidOperationException("Compose.CommandTimeout 必须大于 0 秒");
        if (string.IsNullOrWhiteSpace(config.Compose.StacksPath)
            || string.IsNullOrWhiteSpace(config.Compose.DockerExecutablePath))
        {
            throw new InvalidOperationException("Compose 的应用栈目录和 docker 可执行文件不能为空");
        }
        if (string.IsNullOrWhiteSpace(config.Nginx.ConfigPath)
            || string.IsNullOrWhiteSpace(config.Nginx.GeneratedPath)
            || string.IsNullOrWhiteSpace(config.Nginx.ActivePath)
            || string.IsNullOrWhiteSpace(config.Nginx.StreamPath)
            || string.IsNullOrWhiteSpace(config.Nginx.CertPath)
            || string.IsNullOrWhiteSpace(config.Nginx.ExecutablePath))
        {
            throw new InvalidOperationException("Nginx 路径和可执行文件配置不能为空");
        }
        if (string.IsNullOrWhiteSpace(config.Acme.ExecutablePath)
            || config.Acme.CommandTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("ACME 可执行文件不能为空，命令超时必须大于 0 秒");
        }
        if (config.Logging.EnableFile && string.IsNullOrWhiteSpace(config.Logging.Path))
            throw new InvalidOperationException("启用文件日志时 Logging.Path 不能为空");
        if (config.Logging.RetainDays <= 0)
            throw new InvalidOperationException("Logging.RetainDays 必须大于 0");
        if (!Enum.TryParse<Serilog.Events.LogEventLevel>(config.Logging.Level, true, out _))
            throw new InvalidOperationException("Logging.Level 不是有效的日志级别");
        if (config.Security.SessionTimeout <= 0 || config.Security.MaxLoginAttempts <= 0
            || config.Security.LockoutDuration <= 0)
        {
            throw new InvalidOperationException("安全配置中的超时、登录次数和锁定时长必须大于 0");
        }
        if (config.RateLimit.Average <= 0 || config.RateLimit.PeriodSeconds <= 0
            || config.RateLimit.Burst < 0)
        {
            throw new InvalidOperationException("限流平均请求数和周期必须大于 0，突发容量不能小于 0");
        }
    }

    public static string NormalizeBasePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return "/";

        var normalized = path.Trim();
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        normalized = normalized.TrimEnd('/');
        if (normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Contains('?')
            || normalized.Contains('#'))
        {
            throw new InvalidOperationException("Panel.BasePath 不是有效的 URL 路径");
        }

        return normalized;
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
        NginxConfigValidator.ValidateIdentifier(id, "proxy host id");
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
        NginxConfigValidator.ValidateHost(host);
        var filePath = Path.Combine(_proxyHostsPath, $"{host.Id}.yml");
        try
        {
            host.UpdatedAt = DateTime.UtcNow;
            var content = _serializer.Serialize(host);
            WriteFileAtomically(filePath, content, restrictToOwner: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save proxy host {Id}", host.Id);
            throw;
        }
    }

    public bool DeleteProxyHost(string id)
    {
        NginxConfigValidator.ValidateIdentifier(id, "proxy host id");
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
            throw new InvalidOperationException("Certificate metadata is invalid or unreadable", ex);
        }
    }

    public void SaveCertificates(List<CertificateConfig> certificates)
    {
        var filePath = Path.Combine(_certificatesPath, "certificates.yml");
        try
        {
            var content = _serializer.Serialize(certificates);
            WriteFileAtomically(filePath, content, restrictToOwner: false);
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
